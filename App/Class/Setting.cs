// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;

using global::PowerToys.GPOWrapper;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.PowerToys.Settings.UI.Library.Utilities;

// <summary>
//     Application settings.
// </summary>
// <history>
//     2008 created by Truong Do (ductdo).
//     2009-... modified by Truong Do (TruongDo).
//     2023- Included in PowerToys.
// </history>
using Microsoft.Win32;
using MouseWithoutBorders.Core;
using Settings.UI.Library.Attributes;

using Lock = System.Threading.Lock;
using SettingsHelper = Microsoft.PowerToys.Settings.UI.Library.Utilities.Helper;

[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.Properties.Setting.Values.#LoadIntSetting(System.String,System.Int32)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.Properties.Setting.Values.#SaveSetting(System.String,System.Object)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.Properties.Setting.Values.#LoadStringSetting(System.String,System.String)", Justification = "Dotnet port with style preservation")]
[module: SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes", Scope = "member", Target = "MouseWithoutBorders.Properties.Setting.Values.#SaveSettingQWord(System.String,System.Int64)", Justification = "Dotnet port with style preservation")]

namespace MouseWithoutBorders.Class
{
        internal class Settings
    {
        internal bool Changed;
        private string sharedValues;

        internal MonitorOrigin[] MonitorOrigins
        {
            get { lock (_loadingSettingsLock) return _properties.MonitorOrigins.ToArray(); }
        }

        internal void SetMonitorOrigins(MonitorOrigin[] origins)
        {
            lock (_loadingSettingsLock)
            {
                // Metadata for an offline display may not be present in the editor.
                var updates = origins.ToDictionary(origin => origin.Key);
                var merged = new List<MonitorOrigin>();
                foreach (var origin in _properties.MonitorOrigins)
                    merged.Add(updates.Remove(origin.Key, out var updated) ? updated : origin);
                merged.AddRange(updates.Values);
                MonitorTopology.ApplyOrigins(merged.ToArray());
                _properties.MonitorOrigins = merged;
                CommitSharedSettings();
            }
        }

        internal SharedSettingsSnapshot SharedSnapshot()
        {
            lock (_loadingSettingsLock) return SharedSettingsSnapshot.Capture(_properties);
        }

        internal int[] GetShortcutKeys(KeyboardAction action)
        {
            lock (_loadingSettingsLock)
            {
                if (_properties.ShortcutKeys.TryGetValue(action.ToString(), out var keys)) return keys.ToArray();
                if (action <= KeyboardAction.Computer4)
                    return _properties.HotKeySwitchMachine.Value == 0 ? [] : [(int)VK.CONTROL, (int)VK.MENU, _properties.HotKeySwitchMachine.Value + (int)action];
                var hotkey = action switch
                {
                    KeyboardAction.NextComputer => new HotkeySettings(false, true, true, false, _properties.HotKeySwitchMachine.Value == '1' ? '5' : (int)VK.F5),
                    KeyboardAction.AllComputers => _properties.Switch2AllPCShortcut,
                    KeyboardAction.LockComputers => _properties.LockMachineShortcut,
                    KeyboardAction.Reconnect => _properties.ReconnectShortcut,
                    _ => _properties.ToggleEasyMouseShortcut,
                };
                return new[] { hotkey.Ctrl ? (int)VK.CONTROL : 0, hotkey.Alt ? (int)VK.MENU : 0,
                    hotkey.Shift ? (int)VK.SHIFT : 0, hotkey.Win ? 256 : 0, hotkey.Code }.Where(key => key != 0).ToArray();
            }
        }

        internal void SetShortcutKeys(KeyboardAction action, int[] keys)
        {
            lock (_loadingSettingsLock)
            {
                _properties.ShortcutKeys[action.ToString()] = keys.ToArray();
                CommitSharedSettings();
            }
        }

        internal void CommitSharedSettings()
        {
            lock (_loadingSettingsLock)
            {
                sharedValues = StampSharedSettings(_properties);
                SaveSettings();
            }
        }

        private string StampSharedSettings(MouseWithoutBordersProperties properties)
        {
            string current = JsonSerializer.Serialize(SharedSettingsSnapshot.Capture(properties).Values);
            if (current != sharedValues)
            {
                properties.SharedRevision = checked(properties.SharedRevision + 1);
                properties.SharedWriter = (uint)MachineId;
            }
            return current;
        }

        internal PairingGroup ExportPairedGroup(string peerName, IPAddress address)
        {
            lock (_loadingSettingsLock)
            {
                var candidate = (MouseWithoutBordersProperties)_properties.Clone();
                AddPairingMembers(candidate, Common.MachineName, peerName);
                SetPairingAddress(candidate, peerName, address);
                string values = StampSharedSettings(candidate);
                var group = new PairingGroup(candidate.SecurityKey.Value, candidate.TCPPort.Value, SharedSettingsSnapshot.Capture(candidate));
                SavePairedProperties(candidate, values);
                return group;
            }
        }

        internal void AdoptPairedGroup(string peerName, IPAddress address, PairingGroup group)
        {
            if (!Encryption.IsKeyValid(group.Key, out string error)) throw new InvalidDataException(error);
            var incoming = group.Settings.ReadValues();
            var transport = Common.Sk;
            // Authentication already owns TCP -> settings. Only an unconnected joiner
            // holds this owner through its commit; normal authenticated input is untouched.
            lock (transport?.TcpSocketsLock ?? this)
            lock (_loadingSettingsLock)
            {
                if (Common.Sk != transport || Common.AtLeastOneSocketConnected())
                    throw new InvalidOperationException("This PC is already connected to a group. Disconnect it before joining another group.");
                var candidate = (MouseWithoutBordersProperties)_properties.Clone();
                foreach (var field in SharedSettingsSnapshot.Fields)
                    if (group.Settings.Values.ContainsKey(field.Name)) field.SetValue(candidate, field.GetValue(incoming));
                AddPairingMembers(candidate, Common.MachineName, peerName);
                SetPairingAddress(candidate, peerName, address);
                candidate.SecurityKey.Value = group.Key;
                candidate.TCPPort.Value = group.Port; // The existing storage owner validates the usable TCP pair.
                candidate.SharedRevision = group.Settings.Revision;
                candidate.SharedWriter = group.Settings.Writer;
                string values = JsonSerializer.Serialize(SharedSettingsSnapshot.Capture(candidate).Values);
                SavePairedProperties(candidate, values);
            }
        }

        private static void AddPairingMembers(MouseWithoutBordersProperties properties, params string[] names)
        {
            while (properties.MachineMatrixString.Count < MachineStuff.MAX_MACHINE) properties.MachineMatrixString.Add("");
            foreach (string name in names)
            {
                if (properties.MachineMatrixString.Any(existing => existing.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                int slot = properties.MachineMatrixString.FindIndex(string.IsNullOrWhiteSpace);
                if (slot < 0) throw new InvalidOperationException("The four computer slots are full. Remove an unused computer first.");
                properties.MachineMatrixString[slot] = name;
            }
        }

        private static void SetPairingAddress(MouseWithoutBordersProperties properties, string name, IPAddress address)
        {
            var mappings = properties.Name2IP.Value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !string.Equals(line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), name, StringComparison.OrdinalIgnoreCase));
            properties.Name2IP.Value = string.Join(Environment.NewLine, mappings.Append($"{name} {address}"));
        }

        private void SavePairedProperties(MouseWithoutBordersProperties candidate, string values)
        {
            MonitorTopology.ValidateOrigins(candidate.MonitorOrigins.ToArray());
            var previous = _properties;
            _properties = candidate;
            try { SaveSettingsSynchronously(); }
            catch { _properties = previous; throw; }
            sharedValues = values;
            Changed = true;
        }

        internal bool AcceptSharedSettings(SharedSettingsSnapshot snapshot)
        {
            var incoming = snapshot.ReadValues();
            lock (_loadingSettingsLock)
            {
                if (!snapshot.IsNewerThan(_properties.SharedRevision, _properties.SharedWriter)) return false;
                MonitorTopology.ApplyOrigins(incoming.MonitorOrigins.ToArray(), string.Join(",", incoming.MachineMatrixString));
                foreach (var field in SharedSettingsSnapshot.Fields)
                    if (snapshot.Values.ContainsKey(field.Name)) field.SetValue(_properties, field.GetValue(incoming));
                _properties.SharedRevision = snapshot.Revision;
                _properties.SharedWriter = snapshot.Writer;
                sharedValues = JsonSerializer.Serialize(SharedSettingsSnapshot.Capture(_properties).Values);
                SaveSettings();
                Logger.Log($"Adopted shared settings: revision={snapshot.Revision}, writer={snapshot.Writer}, layout=[{string.Join(",", _properties.MachineMatrixString)}]");
                Changed = true;
                return true;
            }
        }

        private readonly SettingsUtils _settingsUtils;
        private readonly Lock _loadingSettingsLock = new Lock();
        private readonly IFileSystemWatcher _watcher;

        private MouseWithoutBordersProperties _properties;
        private MouseWithoutBordersSettings _settings;

        private string _lastDocument;
        private volatile string _pendingDocument;
        private readonly object _settingsWriteLock = new();
        private bool _writerRunning;
        private bool _pauseInstantSaving;

        // Compound imported UI operations pause saves until their final commit.
        public bool PauseInstantSaving
        {
            get { lock (_loadingSettingsLock) { return _pauseInstantSaving; } }
            set { lock (_loadingSettingsLock) { _pauseInstantSaving = value; } }
        }

        internal void UpdateSettingsFromJson()
        {
            bool sendMatrix = false;
            bool hideCursor = false;
            bool reopen = false;
            string observedDocument, observedMemory;
            lock (_loadingSettingsLock)
            {
                // A watcher notification for our own write must never roll back a
                // later in-memory edit or overwrite a pending save.
                if (_properties != null && (_pendingDocument != null || _pauseInstantSaving)) return;
                observedDocument = _lastDocument;
                observedMemory = _properties == null ? null : SnapshotDocument();
            }
            if (observedMemory == null && !_settingsUtils.SettingsExists("MouseWithoutBorders"))
                new MouseWithoutBordersSettings().Save(_settingsUtils);
            var settings = _settingsUtils.GetSettingsOrDefault<MouseWithoutBordersSettings>("MouseWithoutBorders");
            string document = JsonSerializer.Serialize(settings, SettingsUtils.SerializerOptions);
            lock (_loadingSettingsLock)
            {
                // A commit publishes a new document instance even when its contents are unchanged.
                // Also preserve local edits whose caller has not requested a save yet.
                if (!ReferenceEquals(observedDocument, _lastDocument)
                    || _properties != null && (_pendingDocument != null || _pauseInstantSaving || observedMemory != SnapshotDocument())) return;
                if (_properties != null && document == _lastDocument) return;

                var previous = _properties;
                _settings = settings;
                _properties = settings.Properties;
                _lastDocument = document;
                if (previous != null)
                {
                    sendMatrix = previous.WrapMouse != _properties.WrapMouse
                        || !Enumerable.SequenceEqual(previous.MachineMatrixString, _properties.MachineMatrixString);
                    hideCursor = previous.DrawMouseCursor && !_properties.DrawMouseCursor;
                    if (!previous.MonitorOrigins.SequenceEqual(_properties.MonitorOrigins))
                    {
                        MonitorTopology.ApplyOrigins(_properties.MonitorOrigins.ToArray());
                        sendMatrix = true;
                    }
                    reopen = previous.SecurityKey.Value != _properties.SecurityKey.Value;
                }
            }

            // Network/UI callbacks must run outside the settings lock to avoid lock inversion.
            if (hideCursor) CustomCursor.ShowFakeMouseCursor(int.MinValue, int.MinValue);
            if (reopen)
            {
                Encryption.MyKey = MyKey;
                SocketStuff.InvalidKeyFound = false;
                InitAndCleanup.ReopenSocketDueToReadError = true;
                Common.ReopenSockets(true);
            }
            if (sendMatrix)
            {
                MachineStuff.MachineMatrix = null;
                MachineStuff.SendMachineMatrix();
            }
        }

        internal void SaveKeySynchronously(string key)
        {
            lock (_loadingSettingsLock)
            {
                string previous = _properties.SecurityKey.Value;
                _properties.SecurityKey.Value = key;
                try { SaveSettingsSynchronously(); }
                catch
                {
                    _properties.SecurityKey.Value = previous;
                    throw;
                }
            }
        }

        internal void UpdateAppMode(string appMode)
        {
            lock (_loadingSettingsLock)
            {
                _settings.AppMode = appMode;
                SaveSettingsSynchronously();
            }
        }

        private string SnapshotDocument()
        {
            return JsonSerializer.Serialize(new MouseWithoutBordersSettings
            {
                Name = _settings.Name,
                Version = _settings.Version,
                AppMode = _settings.AppMode,
                Properties = _properties,
            }, SettingsUtils.SerializerOptions);
        }

        public void SaveSettings()
        {
            if (Common.RunOnLogonDesktop) return;
            lock (_loadingSettingsLock)
            {
                // Serialize while holding the owner lock: no mutable object crosses
                // to the worker, and one worker coalesces pending writes in order.
                _pendingDocument = SnapshotDocument();
                if (_writerRunning) return;
                _writerRunning = true;
                _ = Task.Run(WritePendingSettings);
            }
        }

        internal void SaveSettingsSynchronously()
        {
            if (Common.RunOnLogonDesktop) return;
            lock (_loadingSettingsLock)
            lock (_settingsWriteLock)
            {
                string document = SnapshotDocument();
                _settingsUtils.SaveSettings(document, "MouseWithoutBorders");
                _lastDocument = document;
                _pendingDocument = null;
            }
        }

        private void WritePendingSettings()
        {
            for (int attempt = 0; attempt < 5;)
            {
                try
                {
                    string document;
                    lock (_loadingSettingsLock)
                    {
                        document = _pendingDocument;
                        if (document == null)
                        {
                            _writerRunning = false;
                            return;
                        }
                    }
                    // Disk I/O must not hold the lock used by every input-settings read.
                    lock (_settingsWriteLock)
                    {
                        // A newer edit or synchronous commit supersedes this captured document.
                        if (_pendingDocument != document) continue;
                        _settingsUtils.SaveSettings(document, "MouseWithoutBorders");
                    }
                    lock (_loadingSettingsLock)
                    {
                        if (_pendingDocument == document)
                        {
                            _lastDocument = document;
                            _pendingDocument = null;
                        }
                    }
                    attempt = 0;
                }
                catch (Exception ex)
                {
                    Logger.Log("Could not save preferences: " + ex.Message);
                    attempt++;
                    Thread.Sleep(500);
                }
            }
            lock (_loadingSettingsLock) { _writerRunning = false; }
            if (Common.MainForm != null)
            {
                Common.DoSomethingInUIThread(() => Common.ShowToolTip(
                    "Preferences could not be saved. Check that the preferences file and folder are writable, then apply your changes again.",
                    10000, ToolTipIcon.Error, forceEvenIfHidingOldUI: true));
            }
        }

        internal Settings() : this(SettingsUtils.Default, watchChanges: true) { }

        internal Settings(SettingsUtils settingsUtils, bool watchChanges)
        {
            _settingsUtils = settingsUtils;
            UpdateSettingsFromJson();
            sharedValues = JsonSerializer.Serialize(SharedSettingsSnapshot.Capture(_properties).Values);
            if (watchChanges)
            {
                _watcher = SettingsHelper.GetFileWatcher("MouseWithoutBorders", "settings.json", () =>
                {
                    try { UpdateSettingsFromJson(); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        Logger.Log("Preferences reload rejected; keeping current settings: " + ex.Message);
                    }
                });
            }
        }

        internal string Username { get; set; }

        internal bool IsMyKeyRandom { get; set; }

        internal string MachineMatrixString
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return string.Join(",", _properties.MachineMatrixString);
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.MachineMatrixString = new List<string>(value.Split(","));
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal string MachinePoolString
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.MachinePool.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    if (!value.Equals(_properties.MachinePool.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        _properties.MachinePool.Value = value;
                    }
                }
            }
        }

        internal string MyID => Application.ProductName + " Application";

        internal string MyIdEx => Application.ProductName + " Application-Ex";

        internal bool ShareClipboard
        {
            get
            {
                if (GPOWrapper.GetConfiguredMwbClipboardSharingEnabledValue() == GpoRuleConfigured.Disabled)
                {
                    return false;
                }

                lock (_loadingSettingsLock)
                {
                    return _properties.ShareClipboard;
                }
            }

            set
            {
                if (ShareClipboardIsGpoConfigured)
                {
                    return;
                }

                lock (_loadingSettingsLock)
                {
                    _properties.ShareClipboard = value;
                }
            }
        }

        [CmdConfigureIgnore]
        [JsonIgnore]
        internal bool ShareClipboardIsGpoConfigured => GPOWrapper.GetConfiguredMwbClipboardSharingEnabledValue() == GpoRuleConfigured.Disabled;

        internal bool TransferFile
        {
            get
            {
                if (TransferFileIsGpoConfigured)
                {
                    return false;
                }

                lock (_loadingSettingsLock)
                {
                    return _properties.TransferFile;
                }
            }

            set
            {
                if (TransferFileIsGpoConfigured)
                {
                    return;
                }

                lock (_loadingSettingsLock) { _properties.TransferFile = value; }
            }
        }

        internal bool LimitAutomaticClipboardFileSize
        {
            get { lock (_loadingSettingsLock) return _properties.LimitAutomaticClipboardFileSize; }
            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.LimitAutomaticClipboardFileSize = value;
                    CommitSharedSettings();
                }
            }
        }

        [CmdConfigureIgnore]
        [JsonIgnore]
        internal bool TransferFileIsGpoConfigured => ShareClipboardIsGpoConfigured || GPOWrapper.GetConfiguredMwbFileTransferEnabledValue() == GpoRuleConfigured.Disabled;

        internal bool MatrixOneRow
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.MatrixOneRow;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.MatrixOneRow = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal bool MatrixCircle
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.WrapMouse;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.WrapMouse = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal int EasyMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.EasyMouse.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.EasyMouse.Value = value;
                    if (!PauseInstantSaving)
                    {
                        // Easy Mouse can be enabled or disabled through a shortcut, so a save is required.
                        SaveSettings();
                    }
                }
            }
        }

        internal int[] EasyMouseActivationKeys
        {
            get { lock (_loadingSettingsLock) return _properties.EasyMouseActivationKeys; }
            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.EasyMouseActivationKeys = value;
                    if (!PauseInstantSaving) SaveSettings();
                }
            }
        }

        internal bool BlockMouseAtCorners
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.BlockMouseAtScreenCorners;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.BlockMouseAtScreenCorners = value;
                }
            }
        }

        internal bool DisableEasyMouseWhenForegroundWindowIsFullscreen
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.DisableEasyMouseWhenForegroundWindowIsFullscreen;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.DisableEasyMouseWhenForegroundWindowIsFullscreen = value;
                }
            }
        }

        internal HashSet<string> EasyMouseFullscreenSwitchBlockExcludedApps
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.EasyMouseFullscreenSwitchBlockExcludedApps.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.EasyMouseFullscreenSwitchBlockExcludedApps.Value = value;
                }
            }
        }

        internal string Enc(string st, bool dec, DataProtectionScope protectionScope)
        {
            if (st == null || st.Length < 1)
            {
                return string.Empty;
            }

            byte[] ep = Common.GetBytesU(st);
            byte[] rv, st2;

            if (dec)
            {
                st2 = Convert.FromBase64String(st);
                rv = ProtectedData.Unprotect(st2, ep, protectionScope);
                return Common.GetStringU(rv);
            }
            else
            {
                st2 = Common.GetBytesU(st);
                rv = ProtectedData.Protect(st2, ep, protectionScope);
                return Convert.ToBase64String(rv);
            }
        }

        internal string MyKey
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    if (_properties.SecurityKey.Value.Length != 0)
                    {
                        return _properties.SecurityKey.Value;
                    }
                    else
                    {
                        string randomKey = Encryption.CreateDefaultKey();
                        _properties.SecurityKey.Value = randomKey;

                        return randomKey;
                    }
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.SecurityKey.Value = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal int MyKeyDaysToExpire
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return int.MaxValue; // TODO(@yuyoyuppe): do we still need expiration mechanics now?
                }
            }
        }

        // Note(@htcfreek): Settings UI CheckBox is disabled in frmMatrix.cs > FrmMatrix_Load()
        // Note(@htcfreek): If this settings gets implemented in the future we need a Group Policy for it!
        internal bool DisableCAD
        {
            get
            {
                return false;
            }
        }

        // Note(@htcfreek): Settings UI CheckBox is disabled in frmMatrix.cs > FrmMatrix_Load()
        // Note(@htcfreek): If this settings gets implemented in the future we need a Group Policy for it!
        internal bool HideLogonLogo
        {
            get
            {
                return false;
            }
        }

        internal bool HideMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.HideMouseAtScreenEdge;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.HideMouseAtScreenEdge = value;
                }
            }
        }

        internal bool BlockScreenSaver
        {
            get
            {
                if (GPOWrapper.GetConfiguredMwbDisallowBlockingScreensaverValue() == GpoRuleConfigured.Enabled)
                {
                    return false;
                }

                lock (_loadingSettingsLock)
                {
                    return _properties.BlockScreenSaverOnOtherMachines;
                }
            }

            set
            {
                if (BlockScreenSaverIsGpoConfigured)
                {
                    return;
                }

                lock (_loadingSettingsLock)
                {
                    _properties.BlockScreenSaverOnOtherMachines = value;
                }
            }
        }

        [CmdConfigureIgnore]
        [JsonIgnore]
        internal bool BlockScreenSaverIsGpoConfigured => GPOWrapper.GetConfiguredMwbDisallowBlockingScreensaverValue() == GpoRuleConfigured.Enabled;

        internal bool MoveMouseRelatively
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.MoveMouseRelatively;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.MoveMouseRelatively = value;
                }
            }
        }

        internal string LastPersonalizeLogonScr
        {
            get
            {
                return string.Empty;
            }
        }

        internal uint DesMachineID
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return lastDestinationMachineId;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    lastDestinationMachineId = value;
                }
            }
        }

        // Only used for the in-process desktop handoff. A remote destination is never this PC's persistent identity.
        private uint lastDestinationMachineId;

        internal int LastX
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.LastX.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    Common.LastX = value;
                    _properties.LastX.Value = value;
                }
            }
        }

        internal int LastY
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.LastY.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    Common.LastY = value;
                    _properties.LastY.Value = value;
                }
            }
        }

        internal int PackageID
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.PackageID.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.PackageID.Value = value;
                }
            }
        }

        internal bool FirstRun
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.FirstRun;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.FirstRun = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal int HotKeySwitchMachine
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.HotKeySwitchMachine.Value;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.HotKeySwitchMachine.Value = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal bool KeyboardShortcutsEnabled
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.KeyboardShortcutsEnabled;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.KeyboardShortcutsEnabled = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal HotkeySettings HotKeySwitch2AllPC
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.Switch2AllPCShortcut;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.Switch2AllPCShortcut = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal HotkeySettings HotKeyToggleEasyMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.ToggleEasyMouseShortcut;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.ToggleEasyMouseShortcut = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal HotkeySettings HotKeyLockMachine
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.LockMachineShortcut;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.LockMachineShortcut = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal HotkeySettings HotKeyReconnect
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.ReconnectShortcut;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.ReconnectShortcut = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal int HotKeyExitMM
        {
            get
            {
                return 0;
            }
        }

        private int switchCount;

        internal int SwitchCount
        {
            get
            {
                return switchCount;
            }

            set
            {
                switchCount = value;
                if (!PauseInstantSaving)
                {
                    SaveSettings();
                }
            }
        }

        internal int DumpObjectsLevel => 6;

        internal int TcpPort => _properties.TCPPort.Value;

        internal void SetTcpPortSynchronously(int port)
        {
            lock (_loadingSettingsLock)
            {
                int previous = _properties.TCPPort.Value;
                _properties.TCPPort.Value = port;
                try { SaveSettingsSynchronously(); }
                catch { _properties.TCPPort.Value = previous; throw; }
            }
        }

        internal bool DrawMouse
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.DrawMouseCursor;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.DrawMouseCursor = value;
                }
            }
        }

        internal string DefaultReceivingFolder
        {
            get { lock (_loadingSettingsLock) return _properties.DefaultReceivingFolder; }
        }

        internal bool AutoCloseTransferWindow
        {
            get { lock (_loadingSettingsLock) return _properties.AutoCloseTransferWindow; }
        }

        internal void SetDefaultReceivingFolder(string folder)
        {
            folder = TransferReceivePreferences.NormalizeFolder(folder);
            lock (_loadingSettingsLock)
            {
                string previous = _properties.DefaultReceivingFolder;
                _properties.DefaultReceivingFolder = folder;
                try { SaveSettingsSynchronously(); }
                catch { _properties.DefaultReceivingFolder = previous; throw; }
            }
        }

        internal void SetAutoCloseTransferWindow(bool enabled)
        {
            lock (_loadingSettingsLock)
            {
                bool previous = _properties.AutoCloseTransferWindow;
                _properties.AutoCloseTransferWindow = enabled;
                try { SaveSettingsSynchronously(); }
                catch { _properties.AutoCloseTransferWindow = previous; throw; }
            }
        }

        internal bool ReverseLookup
        {
            get
            {
                if (GPOWrapper.GetConfiguredMwbValidateRemoteIpValue() == GpoRuleConfigured.Enabled)
                {
                    return true;
                }
                else if (GPOWrapper.GetConfiguredMwbValidateRemoteIpValue() == GpoRuleConfigured.Disabled)
                {
                    return false;
                }

                lock (_loadingSettingsLock)
                {
                    return _properties.ValidateRemoteMachineIP;
                }
            }

            set
            {
                if (ReverseLookupIsGpoConfigured)
                {
                    return;
                }

                lock (_loadingSettingsLock)
                {
                    _properties.ValidateRemoteMachineIP = value;
                }
            }
        }

        [CmdConfigureIgnore]
        [JsonIgnore]
        internal bool ReverseLookupIsGpoConfigured => GPOWrapper.GetConfiguredMwbValidateRemoteIpValue() == GpoRuleConfigured.Enabled || GPOWrapper.GetConfiguredMwbValidateRemoteIpValue() == GpoRuleConfigured.Disabled;

        internal bool SameSubNetOnly
        {
            get
            {
                if (GPOWrapper.GetConfiguredMwbSameSubnetOnlyValue() == GpoRuleConfigured.Enabled)
                {
                    return true;
                }
                else if (GPOWrapper.GetConfiguredMwbSameSubnetOnlyValue() == GpoRuleConfigured.Disabled)
                {
                    return false;
                }

                lock (_loadingSettingsLock)
                {
#if PORTABLE_SINGLE_FILE
                    // Removed legacy prefix filter; explicit managed policy above still applies.
                    return false;
#else
                    return _properties.SameSubnetOnly;
#endif
                }
            }

            set
            {
                if (SameSubNetOnlyIsGpoConfigured)
                {
                    return;
                }

                lock (_loadingSettingsLock)
                {
                    _properties.SameSubnetOnly = value;
                }
            }
        }

        [CmdConfigureIgnore]
        [JsonIgnore]
        internal bool SameSubNetOnlyIsGpoConfigured => GPOWrapper.GetConfiguredMwbSameSubnetOnlyValue() == GpoRuleConfigured.Enabled || GPOWrapper.GetConfiguredMwbSameSubnetOnlyValue() == GpoRuleConfigured.Disabled;

        internal string Name2IP
        {
            get
            {
                if (GPOWrapper.GetConfiguredMwbDisableUserDefinedIpMappingRulesValue() == GpoRuleConfigured.Enabled)
                {
                    return string.Empty;
                }

                lock (_loadingSettingsLock)
                {
                    return _properties.Name2IP.Value;
                }
            }

            set
            {
                if (Name2IpIsGpoConfigured)
                {
                    return;
                }

                lock (_loadingSettingsLock)
                {
                    _properties.Name2IP.Value = value;
                }
            }
        }

        [CmdConfigureIgnore]
        [JsonIgnore]
        internal bool Name2IpIsGpoConfigured => GPOWrapper.GetConfiguredMwbDisableUserDefinedIpMappingRulesValue() == GpoRuleConfigured.Enabled;

        [CmdConfigureIgnore]
        [JsonIgnore]
        internal string Name2IpPolicyList => GPOWrapper.GetConfiguredMwbPolicyDefinedIpMappingRules();

        [CmdConfigureIgnore]
        [JsonIgnore]
        internal bool Name2IpPolicyListIsGpoConfigured => !string.IsNullOrWhiteSpace(Name2IpPolicyList);

        internal bool FirstCtrlShiftS
        {
            get
            {
                lock (_loadingSettingsLock)
                {
                    return _properties.FirstCtrlShiftS;
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.FirstCtrlShiftS = value;
                }
            }
        }

        // Was a value read from registry on original Mouse Without Border, but default should be true. We wrongly released it as false, so we're forcing true here.
        // This value wasn't changeable from UI, anyway.

        private string deviceId;

        internal string DeviceId
        {
            get
            {
                string newGuid = Guid.NewGuid().ToString();

                if (deviceId == null || deviceId.Length != newGuid.Length)
                {
                    string defaultId = newGuid;
                    lock (_loadingSettingsLock)
                    {
                        _properties.DeviceID = defaultId;
                        deviceId = _properties.DeviceID.Value;

                        if (deviceId.Equals(defaultId, StringComparison.OrdinalIgnoreCase))
                        {
                            return _properties.DeviceID.Value;
                        }
                    }
                }

                return deviceId;
            }
        }

        private volatile int machineId;

        internal int MachineId
        {
            get
            {
                int cached = machineId;
                if (cached != 0) return cached;
                lock (_loadingSettingsLock)
                {
                    if (machineId == 0) machineId = _properties.MachineID.Value;

                    if (machineId == 0)
                    {
                        var newMachineId = Encryption.Ran.Next();
                        _properties.MachineID.Value = newMachineId;
                        machineId = newMachineId;
                        if (!PauseInstantSaving)
                        {
                            SaveSettings();
                        }
                    }
                }

                return machineId;
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.MachineID.Value = value;
                    machineId = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        internal bool OneWayControlMode => false;

        internal bool OneWayClipboardMode => false;

        internal bool ShowClipNetStatus
        {
            get
            {
                lock (_loadingSettingsLock)
                {
#if PORTABLE_SINGLE_FILE
                    return false;
#else
                    return _properties.ShowClipboardAndNetworkStatusMessages;
#endif
                }
            }

            set
            {
                lock (_loadingSettingsLock)
                {
                    _properties.ShowClipboardAndNetworkStatusMessages = value;
                }
            }
        }

        internal bool ShowOriginalUI
        {
            get
            {
                if (GPOWrapper.GetConfiguredMwbUseOriginalUserInterfaceValue() == GpoRuleConfigured.Disabled)
                {
                    return false;
                }

                lock (_loadingSettingsLock)
                {
                    return _properties.ShowOriginalUI;
                }
            }

            set
            {
                if (GPOWrapper.GetConfiguredMwbUseOriginalUserInterfaceValue() == GpoRuleConfigured.Disabled)
                {
                    return;
                }

                lock (_loadingSettingsLock)
                {
                    _properties.ShowOriginalUI = value;
                }
            }
        }

        // If starting the service fails, work in not service mode.
        internal bool UseService
        {
            get
            {
                if (GPOWrapper.GetConfiguredMwbAllowServiceModeValue() == GpoRuleConfigured.Disabled)
                {
                    return false;
                }

                lock (_loadingSettingsLock)
                {
                    return _properties.UseService;
                }
            }

            set
            {
                if (AllowServiceModeIsGpoConfigured)
                {
                    return;
                }

                lock (_loadingSettingsLock)
                {
                    _properties.UseService = value;
                    if (!PauseInstantSaving)
                    {
                        SaveSettings();
                    }
                }
            }
        }

        [CmdConfigureIgnore]
        [JsonIgnore]
        internal bool AllowServiceModeIsGpoConfigured => GPOWrapper.GetConfiguredMwbAllowServiceModeValue() == GpoRuleConfigured.Disabled;

        // Note(@htcfreek): Settings UI CheckBox is disabled in frmMatrix.cs > FrmMatrix_Load()
        internal bool SendErrorLogV2
        {
            get
            {
                return false;
            }
        }
    }

    public static class Setting
    {
        internal static Settings Values = new Settings();
    }
}
