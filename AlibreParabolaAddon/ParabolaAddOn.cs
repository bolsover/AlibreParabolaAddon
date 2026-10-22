using System;
using System.Collections.Generic;
using System.Windows.Forms;
using AlibreAddOn;
using AlibreX;

namespace AlibreParabolaAddon
{
    /// <summary>
    /// IAlibreAddOn: one root menu with one command that returns a live
    /// ParabolaCommand, which tracks the user's sketch-point selection.
    /// </summary>
    public class ParabolaAddOn : IAlibreAddOn
    {
        public const string Title = "Parabola";

        private const int MenuIdRoot = 29500;
        private const int MenuIdCommand = 29501;
        private static readonly int[] SubMenuIds = { MenuIdCommand };

        private readonly IADRoot _root;
        private readonly IntPtr _parentWindow;
        private readonly int _build;

        /// <summary>Open parabola commands, one per session identifier.</summary>
        private readonly Dictionary<string, ParabolaCommand> _commands = new Dictionary<string, ParabolaCommand>();

        public ParabolaAddOn(IADRoot root, IntPtr parentWindow)
        {
            _root = root;
            _parentWindow = parentWindow;
            _build = AlibreVersion.GetBuild(root);
        }

        private bool IsSupportedVersion => _build >= AlibreVersion.MinimumBuild;

        public int RootMenuItem => MenuIdRoot;

        public bool HasSubMenus(int menuId) => menuId == MenuIdRoot;

        public Array SubMenuItems(int menuId) => menuId == MenuIdRoot ? SubMenuIds : new int[0];

        public string MenuItemText(int menuId)
        {
            switch (menuId)
            {
                case MenuIdRoot: return "Parabola";
                case MenuIdCommand: return "Parabola from Two Points";
                default: return string.Empty;
            }
        }

        public bool PopupMenu(int menuId) => menuId == MenuIdRoot;

        public ADDONMenuStates MenuItemState(int menuId, string sessionIdentifier)
        {
            // The root stays enabled so the drop-down (and the tooltip explaining
            // why the command is greyed) can always be opened.
            if (menuId == MenuIdRoot) return ADDONMenuStates.ADDON_MENU_ENABLED;
            return menuId == MenuIdCommand && IsSupportedVersion
                ? ADDONMenuStates.ADDON_MENU_ENABLED
                : ADDONMenuStates.ADDON_MENU_GRAYED;
        }

        public string MenuItemToolTip(int menuId)
        {
            if (menuId != MenuIdCommand) return string.Empty;
            if (!IsSupportedVersion)
                return "Requires Alibre Design build " + AlibreVersion.MinimumBuild + " or later (this is build " + _build + ")";
            return "Select two sketch points or a sketch line, enter a height, and add a parabolic spline";
        }

        /// <summary>Icon file name, looked up in the folder holding the .adc.</summary>
        public string MenuIcon(int menuId) => "AlibreParabolaAddon.ico";

        public bool HasPersistentDataToSave(string sessionIdentifier) => false;

        public IAlibreAddOnCommand InvokeCommand(int menuId, string sessionIdentifier)
        {
            if (menuId != MenuIdCommand) return null;
            try
            {
                if (!IsSupportedVersion)
                {
                    MessageBox.Show(MenuItemToolTip(MenuIdCommand), Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }

                var key = sessionIdentifier ?? string.Empty;
                ParabolaCommand existing;
                if (_commands.TryGetValue(key, out existing))
                {
                    if (existing.IsShowing)
                    {
                        existing.Activate();
                        return null;
                    }

                    // Never got as far as showing its dialog; don't let it block a fresh start.
                    _commands.Remove(key);
                }

                var session = FindSession(sessionIdentifier);
                if (session == null)
                {
                    MessageBox.Show("Open a part and select two sketch points first.", Title,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return null;
                }

                var command = new ParabolaCommand(_root, session, _parentWindow, c =>
                {
                    ParabolaCommand current;
                    if (_commands.TryGetValue(key, out current) && current == c) _commands.Remove(key);
                });
                _commands[key] = command;
                return command;
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString(), Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return null;
        }

        private IADSession FindSession(string sessionIdentifier)
        {
            if (!string.IsNullOrEmpty(sessionIdentifier))
            {
                foreach (IADSession session in _root.Sessions)
                    if (session.Identifier == sessionIdentifier) return session;
            }
            return _root.TopmostSession;
        }

        public void LoadData(IStream pCustomData, string sessionIdentifier)
        {
        }

        public void SaveData(IStream pCustomData, string sessionIdentifier)
        {
        }

        public void setIsAddOnLicensed(bool isLicensed)
        {
        }

        public bool UseDedicatedRibbonTab() => false;
    }
}
