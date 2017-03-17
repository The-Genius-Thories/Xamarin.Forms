using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Android.Support.V4.View;
using Android.Views;
using Xamarin.Forms.Internals;
using AView = Android.Views.View;

namespace Xamarin.Forms.Platform.Android
{
	internal interface IDispatchMotionEvents
	{
		void Signal();
	}

	public abstract class VisualElementRenderer<TElement> : FormsViewGroup, IVisualElementRenderer, AView.IOnTouchListener, AView.IOnClickListener, IEffectControlProvider, IDispatchMotionEvents where TElement : VisualElement
	{
		readonly List<EventHandler<VisualElementChangedEventArgs>> _elementChangedHandlers = new List<EventHandler<VisualElementChangedEventArgs>>();

		readonly Lazy<GestureDetector> _gestureDetector;
		readonly PanGestureHandler _panGestureHandler;
		readonly PinchGestureHandler _pinchGestureHandler;
		readonly TapGestureHandler _tapGestureHandler;

		NotifyCollectionChangedEventHandler _collectionChangeHandler;

		VisualElementRendererFlags _flags = VisualElementRendererFlags.AutoPackage | VisualElementRendererFlags.AutoTrack;

		string _defaultContentDescription;
		bool? _defaultFocusable;
		string _defaultHint;
		int? _defaultLabelFor;
		InnerGestureListener _gestureListener;
		VisualElementPackager _packager;
		PropertyChangedEventHandler _propertyChangeHandler;
		Lazy<ScaleGestureDetector> _scaleDetector;

		protected VisualElementRenderer() : base(Forms.Context)
		{
			_tapGestureHandler = new TapGestureHandler(() => View);
			_panGestureHandler = new PanGestureHandler(() => View, Context.FromPixels);
			_pinchGestureHandler = new PinchGestureHandler(() => View);

			_gestureDetector =
				new Lazy<GestureDetector>(
					() =>
					new GestureDetector(
						_gestureListener =
						new InnerGestureListener(_tapGestureHandler.OnTap, _tapGestureHandler.TapGestureRecognizers, _panGestureHandler.OnPan, _panGestureHandler.OnPanStarted, _panGestureHandler.OnPanComplete)));

			_scaleDetector = new Lazy<ScaleGestureDetector>(
					() => new ScaleGestureDetector(Context, new InnerScaleListener(_pinchGestureHandler.OnPinch, _pinchGestureHandler.OnPinchStarted, _pinchGestureHandler.OnPinchEnded))
					);
		}

		public TElement Element { get; private set; }

		protected bool AutoPackage
		{
			get { return (_flags & VisualElementRendererFlags.AutoPackage) != 0; }
			set
			{
				if (value)
					_flags |= VisualElementRendererFlags.AutoPackage;
				else
					_flags &= ~VisualElementRendererFlags.AutoPackage;
			}
		}

		protected bool AutoTrack
		{
			get { return (_flags & VisualElementRendererFlags.AutoTrack) != 0; }
			set
			{
				if (value)
					_flags |= VisualElementRendererFlags.AutoTrack;
				else
					_flags &= ~VisualElementRendererFlags.AutoTrack;
			}
		}

		View View => Element as View;

		void IEffectControlProvider.RegisterEffect(Effect effect)
		{
			var platformEffect = effect as PlatformEffect;
			if (platformEffect != null)
				OnRegisterEffect(platformEffect);
		}

		void IOnClickListener.OnClick(AView v)
		{
			_tapGestureHandler.OnSingleClick();
		}

		public override bool OnInterceptTouchEvent(MotionEvent ev)
		{
			if (!Element.IsEnabled || (Element.InputTransparent && Element.IsEnabled))
				return true;

			return base.OnInterceptTouchEvent(ev);
		}

		public override bool DispatchTouchEvent(MotionEvent e)
		{
			// TODO hartez 2017/03/17 13:41:06 We need to know what iOS/Windows do for 35477 but with a button/switch/entry	
			// They probably don't fire the frame event, which is fine and should work the same in Android
			// But we need to verify that it's consistent either way

			// TODO hartez 2017/03/17 16:59:44 Clean this up; don't need this logic unless we're in a layout, so we might be able to move this to DefaultRenderer	

			// Excessive explanation time (so I have some idea on Monday morning what the heck I was doing):
			// Normally dispatchTouchEvent feeds the touch events to its children one at a time, top child first,
			// (and only to the children in the hit-test area of the event) stopping as soon as one of them has handled
			// the event. 
			// But to be consistent across the platforms, we don't want this behavior; if an element is not input transparent
			// we don't want an event to "pass through it" and be handled by an element "behind/under" it. We just want the processing
			// to end after the first non-transparent child, regardless of whether the event has been handled.

			// This is only an issue for a couple of controls; the interactive controls (switch, button, slider, etc) already "handle" their touches 
			// and the events don't propagate to other child controls. But for image, label, box that doesn't happen. Also, we can't have those controls 
			// lie about their events being handled because then the events won't propagate to *parent* controls, so a frame with a label in it would
			// never get a tap gesture. In other words, we *want* parent propagation, but *do not want* sibling propagation. So we need to shortcut 
			// base.dispatchTouchEvent in a Layout renderer but still return "false" from our override of dispatchTouchEvent in a Layout renderer.

			// Duplicating the logic of ViewGroup.dispatchTouchEvent and modifying it slightly for our purposes is a non-starter; the method is too
			// complex and does a lot of micro-optimization. Instead, we provide a signalling mechanism for the controls which don't already "handle"* touch
			// events to tell the parent that they will be lying about handling their event to short-circuit base.dispatchTouchEvent. The layout renderer 
			// gets this message and after it gets the "handled" result from dispatchTouchEvent, it then knows to ignore that result and return false/unhandled.
			// This allow the event to propagate up the tree.

			// * That these controls "handle" their touch events might be why Android has the IsEnabled check in onInterceptTouchEvent; we need to test that
			// they will "handle" their events when disabled and that disabled + inputransparent is working correctly. I suspect it's not, and we'll have to 
			// apply the label/image/boxview logic to all the controls.

			_notReallyHandled = false;

			var result = base.DispatchTouchEvent(e);

			if (result)
			{
				if (_notReallyHandled)
				{
					return false;
				}
			}

			return result;
		}

		bool _notReallyHandled;
		void IDispatchMotionEvents.Signal()
		{
			_notReallyHandled = true;
		}

		bool IOnTouchListener.OnTouch(AView v, MotionEvent e)
		{
			if (!Element.IsEnabled)
				return true;

			if (Element.InputTransparent)
				return false;

			var handled = false;
			if (_pinchGestureHandler.IsPinchSupported)
			{
				if (!_scaleDetector.IsValueCreated)
					ScaleGestureDetectorCompat.SetQuickScaleEnabled(_scaleDetector.Value, true);
				handled = _scaleDetector.Value.OnTouchEvent(e);
			}

			_gestureListener?.OnTouchEvent(e);

			if (_gestureDetector.IsValueCreated && _gestureDetector.Value.Handle == IntPtr.Zero)
			{
				// This gesture detector has already been disposed, probably because it's on a cell which is going away
				return handled;
			}

			handled = handled || _gestureDetector.Value.OnTouchEvent(e);

			return handled;
		}

		VisualElement IVisualElementRenderer.Element => Element;

		event EventHandler<VisualElementChangedEventArgs> IVisualElementRenderer.ElementChanged
		{
			add { _elementChangedHandlers.Add(value); }
			remove { _elementChangedHandlers.Remove(value); }
		}

		public virtual SizeRequest GetDesiredSize(int widthConstraint, int heightConstraint)
		{
			Measure(widthConstraint, heightConstraint);
			return new SizeRequest(new Size(MeasuredWidth, MeasuredHeight), MinimumSize());
		}

		void IVisualElementRenderer.SetElement(VisualElement element)
		{
			if (!(element is TElement))
				throw new ArgumentException("element is not of type " + typeof(TElement), nameof(element));

			SetElement((TElement)element);
		}

		public VisualElementTracker Tracker { get; private set; }

		public void UpdateLayout()
		{
			Performance.Start();
			Tracker?.UpdateLayout();
			Performance.Stop();
		}

		public ViewGroup ViewGroup => this;

		public event EventHandler<ElementChangedEventArgs<TElement>> ElementChanged;

		public void SetElement(TElement element)
		{
			if (element == null)
				throw new ArgumentNullException(nameof(element));

			TElement oldElement = Element;
			Element = element;

			Performance.Start();

			if (oldElement != null)
			{
				oldElement.PropertyChanged -= _propertyChangeHandler;
				UnsubscribeGestureRecognizers(oldElement);
			}

			// element may be allowed to be passed as null in the future
			if (element != null)
			{
				Color currentColor = oldElement != null ? oldElement.BackgroundColor : Color.Default;
				if (element.BackgroundColor != currentColor)
					UpdateBackgroundColor();
			}

			if (_propertyChangeHandler == null)
				_propertyChangeHandler = OnElementPropertyChanged;

			element.PropertyChanged += _propertyChangeHandler;
			SubscribeGestureRecognizers(element);

			if (oldElement == null)
			{
				SetOnClickListener(this);
				SetOnTouchListener(this);
				SoundEffectsEnabled = false;
			}

			// must be updated AFTER SetOnClickListener is called
			// SetOnClickListener implicitly calls Clickable = true
			UpdateGestureRecognizers(true);

			OnElementChanged(new ElementChangedEventArgs<TElement>(oldElement, element));

			if (AutoPackage && _packager == null)
				SetPackager(new VisualElementPackager(this));

			if (AutoTrack && Tracker == null)
				SetTracker(new VisualElementTracker(this));

			if (element != null)
				SendVisualElementInitialized(element, this);

			EffectUtilities.RegisterEffectControlProvider(this, oldElement, element);

			if (element != null && !string.IsNullOrEmpty(element.AutomationId))
				SetAutomationId(element.AutomationId);

			SetContentDescription();
			SetFocusable();
			UpdateInputTransparent();

			Performance.Stop();
		}

		/// <summary>
		/// Determines whether the native control is disposed of when this renderer is disposed
		/// Can be overridden in deriving classes 
		/// </summary>
		protected virtual bool ManageNativeControlLifetime => true;

		protected override void Dispose(bool disposing)
		{
			if ((_flags & VisualElementRendererFlags.Disposed) != 0)
				return;
			_flags |= VisualElementRendererFlags.Disposed;

			if (disposing)
			{
				SetOnClickListener(null);
				SetOnTouchListener(null);

				if (Tracker != null)
				{
					Tracker.Dispose();
					Tracker = null;
				}

				if (_packager != null)
				{
					_packager.Dispose();
					_packager = null;
				}

				if (_scaleDetector != null && _scaleDetector.IsValueCreated)
				{
					_scaleDetector.Value.Dispose();
					_scaleDetector = null;
				}

				if (_gestureListener != null)
				{
					_gestureListener.Dispose();
					_gestureListener = null;
				}

				if (ManageNativeControlLifetime)
				{
					int count = ChildCount;
					for (var i = 0; i < count; i++)
					{
						AView child = GetChildAt(i);
						child.Dispose();
					}
				}

				RemoveAllViews();

				if (Element != null)
				{
					Element.PropertyChanged -= _propertyChangeHandler;
					UnsubscribeGestureRecognizers(Element);

					if (Platform.GetRenderer(Element) == this)
						Platform.SetRenderer(Element, null);

					Element = null;
				}
			}

			base.Dispose(disposing);
		}

		protected virtual Size MinimumSize()
		{
			return new Size();
		}

		protected virtual void OnElementChanged(ElementChangedEventArgs<TElement> e)
		{
			var args = new VisualElementChangedEventArgs(e.OldElement, e.NewElement);
			foreach (EventHandler<VisualElementChangedEventArgs> handler in _elementChangedHandlers)
				handler(this, args);

			ElementChanged?.Invoke(this, e);
		}

		protected virtual void OnElementPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == VisualElement.BackgroundColorProperty.PropertyName)
				UpdateBackgroundColor();
			else if (e.PropertyName == Accessibility.HintProperty.PropertyName)
				SetContentDescription();
			else if (e.PropertyName == Accessibility.NameProperty.PropertyName)
				SetContentDescription();
			else if (e.PropertyName == Accessibility.IsInAccessibleTreeProperty.PropertyName)
				SetFocusable();
			else if (e.PropertyName == VisualElement.InputTransparentProperty.PropertyName)
				UpdateInputTransparent();
		}

		protected override void OnLayout(bool changed, int l, int t, int r, int b)
		{
			if (Element == null)
				return;

			ReadOnlyCollection<Element> children = ((IElementController)Element).LogicalChildren;
			foreach (Element element in children)
			{
				var visualElement = element as VisualElement;
				if (visualElement == null)
					continue;

				IVisualElementRenderer renderer = Platform.GetRenderer(visualElement);
				renderer?.UpdateLayout();
			}
		}

		protected virtual void OnRegisterEffect(PlatformEffect effect)
		{
			effect.Container = this;
		}

		protected virtual void SetAutomationId(string id)
		{
			ContentDescription = id;
		}

		protected virtual void SetContentDescription()
		{
			if (Element == null)
				return;

			if (SetHint())
				return;

			if (_defaultContentDescription == null)
				_defaultContentDescription = ContentDescription;

			var elemValue = string.Join(" ", (string)Element.GetValue(Accessibility.NameProperty), (string)Element.GetValue(Accessibility.HintProperty));

			if (!string.IsNullOrWhiteSpace(elemValue))
				ContentDescription = elemValue;
			else
				ContentDescription = _defaultContentDescription;
		}

		protected virtual void SetFocusable()
		{
			if (Element == null)
				return;

			if (!_defaultFocusable.HasValue)
				_defaultFocusable = Focusable;

			Focusable = (bool)((bool?)Element.GetValue(Accessibility.IsInAccessibleTreeProperty) ?? _defaultFocusable);
		}

		protected virtual bool SetHint()
		{
			if (Element == null)
				return false;

			var textView = this as global::Android.Widget.TextView;
			if (textView == null)
				return false;

			// Let the specified Title/Placeholder take precedence, but don't set the ContentDescription (won't work anyway)
			if (((Element as Picker)?.Title ?? (Element as Entry)?.Placeholder ?? (Element as EntryCell)?.Placeholder) != null)
				return true;

			if (_defaultHint == null)
				_defaultHint = textView.Hint;

			var elemValue = string.Join((String.IsNullOrWhiteSpace((string)(Element.GetValue(Accessibility.NameProperty))) || String.IsNullOrWhiteSpace((string)(Element.GetValue(Accessibility.HintProperty)))) ? "" : ". ", (string)Element.GetValue(Accessibility.NameProperty), (string)Element.GetValue(Accessibility.HintProperty));

			if (!string.IsNullOrWhiteSpace(elemValue))
				textView.Hint = elemValue;
			else
				textView.Hint = _defaultHint;

			return true;
		}

		void UpdateInputTransparent()
		{
			InputTransparent = Element.InputTransparent;
		}

		protected void SetPackager(VisualElementPackager packager)
		{
			_packager = packager;
			packager.Load();
		}

		protected void SetTracker(VisualElementTracker tracker)
		{
			Tracker = tracker;
		}

		protected virtual void UpdateBackgroundColor()
		{
			SetBackgroundColor(Element.BackgroundColor.ToAndroid());
		}

		internal virtual void SendVisualElementInitialized(VisualElement element, AView nativeView)
		{
			element.SendViewInitialized(nativeView);
		}

		void HandleGestureRecognizerCollectionChanged(object sender, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs)
		{
			UpdateGestureRecognizers();
		}

		void IVisualElementRenderer.SetLabelFor(int? id)
		{
			if (_defaultLabelFor == null)
				_defaultLabelFor = LabelFor;

			LabelFor = (int)(id ?? _defaultLabelFor);
		}

		void SubscribeGestureRecognizers(VisualElement element)
		{
			var view = element as View;
			if (view == null)
				return;

			if (_collectionChangeHandler == null)
				_collectionChangeHandler = HandleGestureRecognizerCollectionChanged;

			var observableCollection = (ObservableCollection<IGestureRecognizer>)view.GestureRecognizers;
			observableCollection.CollectionChanged += _collectionChangeHandler;
		}

		void UnsubscribeGestureRecognizers(VisualElement element)
		{
			var view = element as View;
			if (view == null || _collectionChangeHandler == null)
				return;

			var observableCollection = (ObservableCollection<IGestureRecognizer>)view.GestureRecognizers;
			observableCollection.CollectionChanged -= _collectionChangeHandler;
		}

		void UpdateClickable(bool force = false)
		{
			var view = Element as View;
			if (view == null)
				return;

			bool newValue = view.ShouldBeMadeClickable();
			if (force || newValue)
				Clickable = newValue;
		}

		void UpdateGestureRecognizers(bool forceClick = false)
		{
			if (View == null)
				return;

			UpdateClickable(forceClick);
		}
	}
}