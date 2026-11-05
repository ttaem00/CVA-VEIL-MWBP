// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System;
using System.ServiceProcess;

namespace MouseWithoutBorders.Core;

internal static class Service
{
    internal static void StartMouseWithoutBordersService(string desktopToRunMouseWithoutBordersOn = null)
    {
        // The input service owns its session/desktop lifecycle; it never launches the full app.
        try
        {
            using var service = new ServiceController(PrivilegedInput.ServiceName);
            if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        }
        catch (Exception error) { Logger.Log("Protected input service: " + error.Message); }
    }

    internal static void StartServiceAndSendLogoffSignal() => StartMouseWithoutBordersService();
}
