using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        Border milestoneMenu;
        FrameworkElement milestoneCard;
        Button firstMilestoneChoice;
        UIElement milestoneReturnFocus;

        void BindMilestoneMenu()
        {
            milestoneMenu = Find<Border>("MilestoneMenuHost");
            Window.PreviewKeyDown += MilestoneMenuKeyDown;
            // WPF routes keys in an open context menu through its own presentation source.
            sort.ContextMenu.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(MilestoneMenuKeyDown), true);
            Window.IsVisibleChanged += (s, e) => { if (!Window.IsVisible) CloseMilestoneMenu(); };
            Window.StateChanged += (s, e) => { if (Window.WindowState == WindowState.Minimized) CloseMilestoneMenu(); };
        }

        static bool IsTextInput(IInputElement focus) => focus is TextBoxBase || focus is PasswordBox;

        void MilestoneMenuKeyDown(object sender, KeyEventArgs e)
        {
            if (!Window.IsActive || !Window.IsVisible || Window.WindowState == WindowState.Minimized ||
                exitInProgress || hidingToTray || zen || zenPlaying) return;

            var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            if (key == Key.Escape && milestoneMenu.IsVisible) {
                CloseMilestoneMenu(true);
                e.Handled = true;
                return;
            }
            if (key != Key.Home || Keyboard.Modifiers != ModifierKeys.None || IsTextInput(Keyboard.FocusedElement)) return;
            e.Handled = true;
            if (!e.IsRepeat && !milestoneMenu.IsVisible) OpenMilestoneMenu();
        }

        void OpenMilestoneMenu()
        {
            if (!milestoneMenu.IsVisible) {
                milestoneReturnFocus = sort.ContextMenu.IsOpen ? sort : Keyboard.FocusedElement as UIElement;
                sort.ContextMenu.IsOpen = false;
                CloseCard();
                if (milestoneCard == null) CreateMilestoneMenu();
                milestoneMenu.Child = milestoneCard;
                milestoneMenu.Visibility = Visibility.Visible;
            }
            if (!firstMilestoneChoice.Focus())
                Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                    if (milestoneMenu.IsVisible && Window.IsActive) firstMilestoneChoice.Focus();
                }));
        }

        void CreateMilestoneMenu()
        {
            milestoneCard = (FrameworkElement)((DataTemplate)Window.Resources["MilestoneMenuCard"]).LoadContent();
            var glass = (LiquidSheet)milestoneCard.FindName("MilestoneMenuGlass");
            glass.Backdrop = sky;
            LiquidLens.SetScene(milestoneCard, glass);
            var choices = (StackPanel)milestoneCard.FindName("MilestoneChoices");
            for (int tier = 1; tier <= StellarMilestone.RewardCount; tier++) {
                var reward = (MilestoneReward)tier;
                string amount = StellarMilestone.RewardAmount(reward), name = StellarMilestone.RewardName(reward);
                var button = new Button { Content = name, Tag = amount, TabIndex = tier - 1 };
                AutomationProperties.SetName(button, amount + " " + name);
                button.Click += (s, e) => sky.PreviewMilestone(reward);
                choices.Children.Add(button);
                if (firstMilestoneChoice == null) firstMilestoneChoice = button;
            }
            ((Button)milestoneCard.FindName("CloseMilestoneMenu")).Click += (s, e) => CloseMilestoneMenu(true);
            ((Button)milestoneCard.FindName("ClearMilestonePreview")).Click += (s, e) => sky.PreviewMilestone(MilestoneReward.None);
        }

        void CloseMilestoneMenu(bool restoreFocus = false)
        {
            if (milestoneMenu == null || milestoneMenu.Child == null) return;
            bool returnFocus = restoreFocus && milestoneMenu.IsKeyboardFocusWithin && Window.IsActive;
            milestoneMenu.Visibility = Visibility.Collapsed;
            milestoneMenu.Child = null;
            sky.PreviewMilestone(MilestoneReward.None);
            if (returnFocus && (milestoneReturnFocus?.IsVisible != true || !milestoneReturnFocus.Focus())) Window.Focus();
            milestoneReturnFocus = null;
        }
    }
}
