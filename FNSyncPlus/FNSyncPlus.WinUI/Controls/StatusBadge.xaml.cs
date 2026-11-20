using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Controls
{
    public sealed partial class StatusBadge : UserControl
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBadge),
                new PropertyMetadata(string.Empty, OnTextChanged));

        public static readonly DependencyProperty ToneProperty =
            DependencyProperty.Register(nameof(Tone), typeof(string), typeof(StatusBadge),
                new PropertyMetadata("Neutral", OnToneChanged));

        public static readonly DependencyProperty ShowDotProperty =
            DependencyProperty.Register(nameof(ShowDot), typeof(bool), typeof(StatusBadge),
                new PropertyMetadata(true, OnShowDotChanged));

        public static readonly DependencyProperty MaxTextWidthProperty =
            DependencyProperty.Register(nameof(MaxTextWidth), typeof(double), typeof(StatusBadge),
                new PropertyMetadata(180.0, OnMaxTextWidthChanged));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public string Tone
        {
            get => (string)GetValue(ToneProperty);
            set => SetValue(ToneProperty, value);
        }

        public bool ShowDot
        {
            get => (bool)GetValue(ShowDotProperty);
            set => SetValue(ShowDotProperty, value);
        }

        public double MaxTextWidth
        {
            get => (double)GetValue(MaxTextWidthProperty);
            set => SetValue(MaxTextWidthProperty, value);
        }

        public StatusBadge()
        {
            InitializeComponent();
            Loaded += (_, _) => ApplyVisualState();
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StatusBadge badge)
            {
                string text = e.NewValue as string ?? string.Empty;
                badge.PillText.Text = text;
                ToolTipService.SetToolTip(badge.PillBorder, string.IsNullOrWhiteSpace(text) ? null : text);
            }
        }

        private static void OnToneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StatusBadge badge) badge.ApplyVisualState();
        }

        private static void OnShowDotChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StatusBadge badge)
            {
                badge.PillDot.Visibility = (e.NewValue is bool show && show) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static void OnMaxTextWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StatusBadge badge && e.NewValue is double max && max > 0)
            {
                badge.PillText.MaxWidth = max;
            }
        }

        private void ApplyVisualState()
        {
            string state = Tone switch
            {
                "Success" or "Info" or "Warning" or "Error" or "Accent" => Tone,
                _ => "Neutral"
            };
            VisualStateManager.GoToState(this, state, false);
        }
    }
}
