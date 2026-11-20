using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace CliTray.Ui
{
    internal static class Dialogs
    {
        public static void Info(Window owner, string title, string message)
        {
            var box = new MessageBox
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "确定",
                IsCloseButtonEnabled = false,
                WindowStartupLocation = OwnerLocation(owner)
            };
            if (owner != null && owner.IsVisible)
                box.Owner = owner;
            Run(box);
        }

        public static bool Confirm(Window owner, string title, string message)
        {
            var box = new MessageBox
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "确定",
                IsSecondaryButtonEnabled = false,
                CloseButtonText = "取消",
                WindowStartupLocation = OwnerLocation(owner)
            };
            if (owner != null && owner.IsVisible)
                box.Owner = owner;
            return Run(box) == UiMessageBoxResult.Primary;
        }

        private static UiMessageBoxResult Run(MessageBox box)
        {
            UiMessageBoxResult result = UiMessageBoxResult.None;
            var frame = new DispatcherFrame();

            box.ShowDialogAsync(true, CancellationToken.None)
                .ContinueWith(
                    delegate(Task<UiMessageBoxResult> task)
                    {
                        result = task.Status == TaskStatus.RanToCompletion
                            ? task.Result
                            : UiMessageBoxResult.None;
                        frame.Continue = false;
                    },
                    TaskScheduler.FromCurrentSynchronizationContext());

            Dispatcher.PushFrame(frame);
            return result;
        }

        private static WindowStartupLocation OwnerLocation(Window owner)
        {
            return owner != null && owner.IsVisible
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
        }
    }
}
