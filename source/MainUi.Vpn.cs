using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace AstralSplit
{
    internal sealed partial class MainUi
    {
        Button vpnSelector;
        TextBlock connectionExplanation;
        string vpnAdapterId, vpnPreferenceError;
        bool vpnPreferenceLoaded, savingVpnPreference;
        int vpnRevision;

        bool CanChooseVpn => worker == null && !promoting && !restoring && !stopping && TrialWorkerPhase == null &&
            !recoveryPending && !trialRecoveryCheckPending && !closingAfterSave && !exitInProgress && !savingVpnPreference;
        bool VpnPreferenceReady => vpnPreferenceLoaded && vpnPreferenceError == null && !savingVpnPreference;
        bool UsingGenericVpn => vpnAdapterId != null && (network?.GenericVpn ?? true);
        string TunnelName => UsingGenericVpn ? "所选 VPN" : "星云";

        void BindVpnControls()
        {
            vpnSelector = Find<Button>("VpnAdapter");
            connectionExplanation = Find<TextBlock>("ConnectionExplanation");
        }

        void PaintVpnControls()
        {
            vpnSelector.IsEnabled = CanChooseVpn;
            string current = vpnPreferenceError != null ? "需要重新选择网卡" :
                vpnAdapterId == null ? "星云自动识别" : network?.Astral?.Name ?? "已选 VPN 网卡未连接";
            vpnSelector.ToolTip = current;
            AutomationProperties.SetHelpText(vpnSelector, current);
            connectionExplanation.Text = UsingGenericVpn ?
                "分流按程序路径匹配。重连时会使用新规则。已接管的 DNS 请求会跟随应用出口，局域网和 Windows 的共享 DNS 仍按系统设置。连接记录显示的是交给哪张网卡。"
                : "分流按程序路径匹配，已有连接会在重连后使用新规则。星云会按自己的设置处理收到的连接，这里还没有检测每个应用实际使用的公网出口。";
        }

        void OpenVpnMenu()
        {
            if (!CanChooseVpn) return;
            var menu = new ContextMenu {
                PlacementTarget = vpnSelector, Placement = PlacementMode.Custom,
                Style = (Style)Window.Resources["SortMenu"], MinWidth = 180, MaxWidth = 340, MaxHeight = 300
            };
            menu.CustomPopupPlacementCallback = (popup, button, offset) => new[] {
                new CustomPopupPlacement(new Point(button.Width - popup.Width + 10, button.Height), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(button.Width - popup.Width + 10, -popup.Height + 17), PopupPrimaryAxis.Horizontal)
            };
            LiquidLens.SetScene(menu, Find<StarfieldLayer>("Sky"));
            void Add(string title, string id, string detail, bool enabled = true)
            {
                var item = new MenuItem {
                    Header = title, ToolTip = detail, IsEnabled = enabled, IsCheckable = enabled || id != null,
                    IsChecked = (enabled || id != null) && vpnPreferenceLoaded && vpnPreferenceError == null &&
                        String.Equals(id, vpnAdapterId, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (sender, args) => ChooseVpn(id);
                menu.Items.Add(item);
            }
            Add("星云自动识别", null, "使用星云加速器的 TUN 网卡");
            var candidates = (network?.Adapters ?? new Adapter[0])
                .Where(NetworkState.EligibleVpn).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (vpnAdapterId != null && !candidates.Any(a => VpnPreference.Matches(a.Id, vpnAdapterId)))
                Add("已选 VPN 网卡未连接", vpnAdapterId, "重新连接原 VPN 网卡，或选择另一个出口", false);
            foreach (var adapter in candidates)
                Add(adapter.Name, Guid.Parse(adapter.Id).ToString("D"), adapter.Description);
            if (candidates.Length == 0 && vpnAdapterId == null)
                Add("没有检测到在线 VPN 网卡", null, "先连接 VPN，再点刷新", false);
            vpnSelector.ContextMenu = menu;
            menu.IsOpen = true;
        }

        async void ChooseVpn(string id)
        {
            if (!CanChooseVpn || VpnPreferenceReady && String.Equals(id, vpnAdapterId, StringComparison.OrdinalIgnoreCase)) return;
            savingVpnPreference = true;
            UpdateState();
            try
            {
                await Task.Run(() => Files.Write(VpnPreference.PathIn(Files.Home), id));
                vpnRevision++;
                vpnAdapterId = id;
                vpnPreferenceLoaded = true;
                vpnPreferenceError = null;
                network = null;
                nextNetwork = DateTime.MinValue;
                message = null;
            }
            catch (Exception ex) { message = "VPN 网卡设置未保存。 " + ex.Message; }
            finally
            {
                savingVpnPreference = false;
                if (!closed) { UpdateNetwork(); UpdateState(); }
            }
        }
    }
}
