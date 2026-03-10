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

        // 鼠标悬停时显示关闭按钮
        private void Window_MouseEnter(object sender, MouseEventArgs e) => BtnClose.Visibility = Visibility.Visible;
        private void Window_MouseLeave(object sender, MouseEventArgs e) => BtnClose.Visibility = Visibility.Hidden;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => this.Hide();

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
        }
    }
}