using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NetworkMonitor
{
    public partial class MiniWindow : Window
    {
        private MainWindow _main;

        public MiniWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;
        }

        // 拖拽窗口
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
        private void Window_MouseEnter(object sender, MouseEventArgs e) => TopRightPanel.Visibility = Visibility.Visible;
        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            // 只有当设置菜单未打开时才隐藏，防止菜单闪烁消失
            if (BtnSettings.ContextMenu == null || !BtnSettings.ContextMenu.IsOpen)
            {
                TopRightPanel.Visibility = Visibility.Hidden;
            }
        }
        private void BtnClose_Click(object sender, RoutedEventArgs e) => this.Hide();

        // 设置按钮点击弹出菜单
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (BtnSettings.ContextMenu != null)
            {
                BtnSettings.ContextMenu.PlacementTarget = BtnSettings;
                BtnSettings.ContextMenu.IsOpen = true;
                BtnSettings.ContextMenu.Closed += (s, args) =>
                {
                    // 菜单关闭后检查鼠标是否还在窗口内，不在则隐藏按钮组
                    if (!this.IsMouseOver) TopRightPanel.Visibility = Visibility.Hidden;
                };
            }
        }

        // 切换置顶状态
        private void MenuTopmost_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem item)
            {
                this.Topmost = item.IsChecked;
            }
        }

        // 调整透明度
        private void MenuOpacity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem item && double.TryParse(item.Tag?.ToString(), out double opacity))
            {
                MainBorder.Opacity = opacity; // 改变边框透明度以保留底层窗口事件捕获能力
            }
        }

        // 接收主界面的数据并重绘

        public void UpdateData(PointCollection ptsDown, PointCollection ptsUp, string downText, string upText, SolidColorBrush bg, SolidColorBrush brushDown, SolidColorBrush brushUp)
        {
            if (this.Visibility != Visibility.Visible) return;

            // 同步主题配色
            MainBorder.Background = new SolidColorBrush(Color.FromArgb(230, bg.Color.R, bg.Color.G, bg.Color.B)); // 90% 不透明度
            MainBorder.BorderBrush = (SolidColorBrush)_main.Resources["BorderMainBrush"];

            PolyDown.Stroke = TxtDown.Foreground = brushDown;
            PolyUp.Stroke = TxtUp.Foreground = brushUp;
            PolyDown.Fill = new SolidColorBrush(Color.FromArgb(30, brushDown.Color.R, brushDown.Color.G, brushDown.Color.B));
            PolyUp.Fill = new SolidColorBrush(Color.FromArgb(30, brushUp.Color.R, brushUp.Color.G, brushUp.Color.B));

            // 更新曲线和文字
            PolyDown.Points = ptsDown;
            PolyUp.Points = ptsUp;
            TxtDown.Text = downText;
            TxtUp.Text = upText;

            // 同步最大坐标和时间跨度，无需改变主窗口调用逻辑，直接反射/提取 XAML 对象
            if (_main.FindName("LabelMax") is System.Windows.Controls.TextBlock lblMax)
            {
                TxtMaxY.Text = lblMax.Text;
            }

            // 动态获取主窗口当前的时间跨度设置
            if (_main.FindName("CustomTimePanel") is System.Windows.Controls.StackPanel customPnl && customPnl.Visibility == Visibility.Visible)
            {
                if (_main.FindName("TxtCustomTime") is System.Windows.Controls.TextBox txtCustom)
                    TxtTimeSpan.Text = txtCustom.Text + " 秒";
            }
            else if (_main.FindName("TimeWindowCombo") is System.Windows.Controls.ComboBox cb && cb.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                TxtTimeSpan.Text = item.Content?.ToString() ?? "未知";
            }
        }
    }
}