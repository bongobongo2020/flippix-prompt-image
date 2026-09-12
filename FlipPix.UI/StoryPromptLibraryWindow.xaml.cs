using System.ComponentModel;
using System.Windows;
using FlipPix.UI.ViewModels;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace FlipPix.UI
{
    /// <summary>
    /// 📚 Story Prompts — see <see cref="StoryPromptLibraryViewModel"/>. The window only supplies the two
    /// dialogs the view model asks through and stops a close that would lose unsaved edits.
    /// </summary>
    public partial class StoryPromptLibraryWindow : Window
    {
        public StoryPromptLibraryWindow(StoryPromptLibraryViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = viewModel;
            viewModel.ConfirmUnsaved = AskUnsaved;
            viewModel.Confirm = AskYesNo;
            Closing += OnClosing;
        }

        public StoryPromptLibraryViewModel ViewModel { get; }

        private UnsavedChoice AskUnsaved(string message)
        {
            var answer = MessageBox.Show(this, message + "\n\nSave them before moving on?", "Story Prompts",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return answer switch
            {
                MessageBoxResult.Yes => UnsavedChoice.Save,
                MessageBoxResult.No => UnsavedChoice.Discard,
                _ => UnsavedChoice.Stay
            };
        }

        private bool AskYesNo(string message) =>
            MessageBox.Show(this, message, "Story Prompts", MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes;

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (!ViewModel.ResolveUnsaved()) e.Cancel = true;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
