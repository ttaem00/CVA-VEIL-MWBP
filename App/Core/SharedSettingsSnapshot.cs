// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.PowerToys.Settings.UI.Library;

namespace MouseWithoutBorders.Core;

internal sealed class SharedSettingsSnapshot
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public uint Writer { get; set; }
    public Dictionary<string, JsonElement> Values { get; set; } = new();
    public MonitorDescriptor[] Displays { get; set; }

    // Only group preferences cross the wire. Identity, secrets, paths and network mappings remain local.
    internal static readonly PropertyInfo[] Fields = ("MachineMatrixString MonitorOrigins MatrixOneRow WrapMouse ShareClipboard TransferFile LimitAutomaticClipboardFileSize " +
        "HideMouseAtScreenEdge BlockScreenSaverOnOtherMachines MoveMouseRelatively BlockMouseAtScreenCorners " +
        "EasyMouse EasyMouseActivationKeys KeyboardShortcutsEnabled HotKeySwitchMachine ToggleEasyMouseShortcut " +
        "LockMachineShortcut ReconnectShortcut Switch2AllPCShortcut DisableEasyMouseWhenForegroundWindowIsFullscreen ShortcutKeys")
        .Split(' ').Select(name => typeof(MouseWithoutBordersProperties).GetProperty(name)!).ToArray();

    internal static SharedSettingsSnapshot Capture(MouseWithoutBordersProperties properties) => new()
    {
        Revision = properties.SharedRevision, Writer = properties.SharedWriter,
        Values = Fields.ToDictionary(field => field.Name, field => JsonSerializer.SerializeToElement(
            field.Name == "MachineMatrixString" ? properties.MachineMatrixString.Concat(Enumerable.Repeat("", 4 - properties.MachineMatrixString.Count)).ToList() : field.GetValue(properties), field.PropertyType, SettingsUtils.SerializerOptions)),
    };

    internal MouseWithoutBordersProperties ReadValues()
    {
        if (Version != 1 || Revision < 0 || Values == null)
            throw new InvalidDataException("Invalid shared settings snapshot.");
        var result = new MouseWithoutBordersProperties();
        foreach (var field in Fields)
        {
            if (!Values.TryGetValue(field.Name, out var json))
            {
                // This extension is absent on existing peers; keep the original required fields strict.
                if (field.Name is nameof(MouseWithoutBordersProperties.ShortcutKeys) or nameof(MouseWithoutBordersProperties.LimitAutomaticClipboardFileSize)) continue;
                throw new InvalidDataException("Missing shared preference: " + field.Name);
            }
            var value = json.Deserialize(field.PropertyType, SettingsUtils.SerializerOptions);
            if (value == null) throw new InvalidDataException("Null shared preference: " + field.Name);
            field.SetValue(result, value);
        }
        if (result.MachineMatrixString.Count != 4 || result.MachineMatrixString.Any(name => name == null || name.Length > 32)
            || !Enum.IsDefined((Class.EasyMouseOption)result.EasyMouse.Value)
            || result.EasyMouseActivationKeys.Any(key => key < 1 || key > 256)
            || result.ShortcutKeys.Values.Any(keys => keys == null))
            throw new InvalidDataException("Invalid shared layout or activation keys.");
        return result;
    }

    internal bool IsNewerThan(long revision, uint writer) => Revision > revision || Revision == revision && Writer > writer;
}

internal static class SharedSettingsFrame
{
    internal const int MaximumBytes = 16384;
    internal static byte[] Encode(SharedSettingsSnapshot snapshot) => JsonSerializer.SerializeToUtf8Bytes(snapshot);
    internal static int PaddedLength(int length)
    {
        if (length < 1 || length > MaximumBytes) throw new InvalidDataException("Shared settings frame is too large or empty.");
        return (length + 31) / 32 * 32;
    }
    internal static SharedSettingsSnapshot Read(Stream stream, int length, bool authenticated)
    {
        if (!authenticated) throw new InvalidDataException("Shared settings require an authenticated connection.");
        byte[] body = new byte[PaddedLength(length)];
        stream.ReadExactly(body);
        try { return JsonSerializer.Deserialize<SharedSettingsSnapshot>(body.AsSpan(0, length)); }
        catch (JsonException error)
        {
            Logger.Log("Ignoring malformed shared settings frame: " + error.Message);
            return null;
        }
    }
}
