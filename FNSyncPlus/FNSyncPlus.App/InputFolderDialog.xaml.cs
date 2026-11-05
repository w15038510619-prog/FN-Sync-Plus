using System.Windows;
using System.Windows.Controls;
using FNSyncPlus.Core.Utils;

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

        private void TxtFolderName_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateInput();
        }

        private bool ValidateInput()
        {
            string name = TxtFolderName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                TxtValidationError.Text = "文件夹名称不能为空";
                TxtValidationError.Visibility = Visibility.Visible;
                BtnOk.IsEnabled = false;
                return false;
            }

            if (!PathHelper.IsValidDirectoryName(name, out string errorMsg))
            {
                TxtValidationError.Text = errorMsg;
                TxtValidationError.Visibility = Visibility.Visible;
                BtnOk.IsEnabled = false;
                return false;
            }

            TxtValidationError.Visibility = Visibility.Collapsed;
            BtnOk.IsEnabled = true;
            return true;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInput()) return;

            FolderName = TxtFolderName.Text.Trim();
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
