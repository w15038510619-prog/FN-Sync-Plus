using System.Windows;

namespace FNSyncPlus.App
{
    public partial class InputFolderDialog : Window
    {
        public string FolderName { get; private set; } = string.Empty;

        public InputFolderDialog(string defaultName = "新文件夹")
        {
            InitializeComponent();
            TxtFolderName.Text = defaultName;
            TxtFolderName.SelectAll();
            TxtFolderName.Focus();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtFolderName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                System.Windows.MessageBox.Show("文件夹名称不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            FolderName = name;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
