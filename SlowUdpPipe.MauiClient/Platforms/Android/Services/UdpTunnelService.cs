﻿using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Widget;
using AndroidX.Core.App;
using Ax.Fw.DependencyInjection;
using Ax.Fw.Extensions;
using Ax.Fw.SharedTypes.Interfaces;
using SlowUdpPipe.Common.Toolkit;
using SlowUdpPipe.MauiClient.Data;
using SlowUdpPipe.MauiClient.Interfaces;
using System.Reactive.Linq;

namespace SlowUdpPipe.MauiClient.Platforms.Android.Services;

[Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public class UdpTunnelService : global::Android.App.Service, IUdpTunnelService, IAppModule<UdpTunnelService>
{
  public static UdpTunnelService ExportInstance(IAppDependencyCtx _ctx) => new();

  private const string SERVICE_NOTIFICATION_CHANNEL = "ServiceChannel";
  private const string GENERAL_NOTIFICATION_CHANNEL = "GeneralChannel";
  private const string STOP_TUNNEL_ID_ACTION_EXTRA = "tunnel-id";
  private const int SERVICE_NOTIFICATION_ID = 100;
  private const int BATTERY_OPTIMIZATION_NOTIFICATION_ID = 200;
  private const int REQUEST_POST_NOTIFICATIONS = 1000;
  private readonly NotificationManager p_notificationManager;
  private readonly IReadOnlyLifetime p_lifetime;
  private readonly IUdpTunnelCtrl p_udpTunnelCtrl;
  private readonly ITunnelsConfCtrl p_tunnelsConfCtrl;
  private ILifetime? p_serviceLifetime;
  private bool p_batteryOptimizationWarnDisplayed = false;

  public UdpTunnelService()
  {
    var context = global::Android.App.Application.Context;
    p_notificationManager = (NotificationManager)context.GetSystemService(NotificationService)!;

    p_lifetime = MauiProgram.Container.Locate<IReadOnlyLifetime>();
    p_udpTunnelCtrl = MauiProgram.Container.Locate<IUdpTunnelCtrl>();
    p_tunnelsConfCtrl = MauiProgram.Container.Locate<ITunnelsConfCtrl>();
  }

  public override IBinder OnBind(Intent? _intent) => throw new NotImplementedException();

  [return: GeneratedEnum]
  public override StartCommandResult OnStartCommand(
    Intent? _intent,
    [GeneratedEnum] StartCommandFlags _flags,
    int _startId)
  {
    if (_intent?.Action == "START_SERVICE")
    {
      p_serviceLifetime = p_lifetime.GetChildLifetime();
      if (p_serviceLifetime == null)
        return StartCommandResult.NotSticky;

      var notification = BuildOrUpdateServiceNotification(Array.Empty<TunnelStatWithName>(), true, p_serviceLifetime.Token);

      if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
#pragma warning disable CA1416 // Validate platform compatibility
        StartForeground(SERVICE_NOTIFICATION_ID, notification, global::Android.Content.PM.ForegroundService.TypeDataSync);
#pragma warning restore CA1416 // Validate platform compatibility
      else
        StartForeground(SERVICE_NOTIFICATION_ID, notification);

      p_udpTunnelCtrl.TunnelsStats
        .Buffer(TimeSpan.FromSeconds(3))
        .Merge(p_udpTunnelCtrl.TunnelsStats.Take(1).ToList())
        .Subscribe(_list =>
        {
          if (!_list.Any())
            return;

          var uniqueTunnelsEE = _list
            .DistinctBy(_ => _.TunnelGuid)
            .OrderBy(_ => _.TunnelName);

          var listAvg = new List<TunnelStatWithName>();
          foreach (var tunnel in uniqueTunnelsEE)
          {
            var rxAvg = _list.Where(_ => _.TunnelGuid == tunnel.TunnelGuid).Average(_ => (long)_.RxBytePerSecond);
            var txAvg = _list.Where(_ => _.TunnelGuid == tunnel.TunnelGuid).Average(_ => (long)_.TxBytePerSecond);
            listAvg.Add(new TunnelStatWithName(tunnel.TunnelGuid, tunnel.TunnelName, (ulong)txAvg, (ulong)rxAvg));
          }

          BuildOrUpdateServiceNotification(listAvg, false, p_serviceLifetime.Token);
        }, p_serviceLifetime);

      p_serviceLifetime.DoOnEnding(() =>
      {
        StopForeground(StopForegroundFlags.Remove);
        StopSelfResult(_startId);
      });

      ShowBatteryWarningNotificationIfNeeded();

      return StartCommandResult.NotSticky;
    }
    else if (_intent?.Action == "STOP_SERVICE")
    {
      p_serviceLifetime?.Dispose();
    }

    return StartCommandResult.NotSticky;
  }

  public void Start()
  {
    var context = global::Android.App.Application.Context;
    var intent = new Intent(context, typeof(UdpTunnelService));
    intent.SetAction("START_SERVICE");
    context.StartForegroundService(intent);
  }

  public void Stop()
  {
    var context = global::Android.App.Application.Context;
    var intent = new Intent(context, Class);
    intent.SetAction("STOP_SERVICE");
    context.StartService(intent);
  }

  private Notification BuildOrUpdateServiceNotification(
    IReadOnlyList<TunnelStatWithName> _tunnels,
    bool _firstShow,
    CancellationToken _ct)
  {
    if (_firstShow && Build.VERSION.SdkInt > BuildVersionCodes.SV2 && Platform.CurrentActivity != null)
      if (ActivityCompat.CheckSelfPermission(Platform.CurrentActivity, "android.permission.POST_NOTIFICATIONS") != global::Android.Content.PM.Permission.Granted)
        ActivityCompat.RequestPermissions(Platform.CurrentActivity, ["android.permission.POST_NOTIFICATIONS"], REQUEST_POST_NOTIFICATIONS);

    if (_firstShow)
    {
      var channel = new NotificationChannel(SERVICE_NOTIFICATION_CHANNEL, "Notify when tunnels' states are changed", NotificationImportance.Min);
      channel.SetShowBadge(false);
      p_notificationManager.CreateNotificationChannel(channel);
    }

    var context = global::Android.App.Application.Context;
    var openAppIntent = PendingIntent.GetActivity(context, 0, Platform.CurrentActivity?.Intent, PendingIntentFlags.Immutable);

    var title = $"{_tunnels.Count} tunnels is up";
    var text = string.Empty;
    foreach (var tunnel in _tunnels)
      text += $"[{tunnel.TunnelName}] Rx: {Converters.BytesPerSecondToString(tunnel.RxBytePerSecond)}; Tx: {Converters.BytesPerSecondToString(tunnel.TxBytePerSecond)}\n";

    text = text.TrimEnd('\n');

    //var layoutSmall = new RemoteViews(context.PackageName, Resource.Layout.notification_small);
    //layoutSmall.SetTextViewText(Resource.Id.notification_small_title, title);

    //var layoutLarge = new RemoteViews(context.PackageName, Resource.Layout.notification_large);
    //layoutLarge.SetTextViewText(Resource.Id.notification_large_title, title);
    //layoutLarge.SetTextViewText(Resource.Id.notification_large_body, text);

    var builder = new Notification.Builder(this, SERVICE_NOTIFICATION_CHANNEL)
     .SetContentIntent(openAppIntent)
     .SetSmallIcon(Resource.Drawable.infinity)
     .SetOnlyAlertOnce(true)
     .SetOngoing(true)
     //.SetStyle(new Notification.DecoratedCustomViewStyle())
     //.SetCustomContentView(layoutSmall)
     //.SetCustomBigContentView(layoutLarge)
     .SetContentTitle(title)
     .SetContentText(text);

#pragma warning disable CA1416 // Validate platform compatibility
    if (_firstShow && Build.VERSION.SdkInt >= BuildVersionCodes.S)
      builder = builder.SetForegroundServiceBehavior(1); // FOREGROUND_SERVICE_IMMEDIATE
#pragma warning restore CA1416 // Validate platform compatibility

    Notification notification = builder.Build();

    if (!_ct.IsCancellationRequested)
      p_notificationManager.Notify(SERVICE_NOTIFICATION_ID, notification);

    return notification;
  }

  private void ShowBatteryWarningNotificationIfNeeded()
  {
    if (p_batteryOptimizationWarnDisplayed)
      return;

    var context = global::Android.App.Application.Context;
    var packageName = context.PackageName;
    var powerMgr = context.GetSystemService(PowerService) as PowerManager;
    var ignoring = powerMgr?.IsIgnoringBatteryOptimizations(packageName);
    if (ignoring != true)
    {
      if (Build.VERSION.SdkInt > BuildVersionCodes.SV2 && Platform.CurrentActivity != null)
        ActivityCompat.RequestPermissions(Platform.CurrentActivity, new[] { "android.permission.POST_NOTIFICATIONS" }, REQUEST_POST_NOTIFICATIONS);

      var channel = new NotificationChannel(GENERAL_NOTIFICATION_CHANNEL, "General events", NotificationImportance.High);
      channel.SetShowBadge(true);
      p_notificationManager.CreateNotificationChannel(channel);

      var intent = new Intent();
      intent.SetFlags(ActivityFlags.NewTask);
      intent.SetAction(global::Android.Provider.Settings.ActionApplicationDetailsSettings);
      var uri = global::Android.Net.Uri.Parse($"package:{packageName}");
      intent.SetData(uri);
      intent.AddCategory(Intent.CategoryDefault);
      var notificationIntent = PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.Immutable);

      var layoutSmall = new RemoteViews(context.PackageName, Resource.Layout.notification_small);
      layoutSmall.SetTextViewText(Resource.Id.notification_small_title, "Battery optimization is enabled");

      var layoutLarge = new RemoteViews(context.PackageName, Resource.Layout.notification_large);
      layoutLarge.SetTextViewText(Resource.Id.notification_large_title, "Battery optimization is enabled");
      layoutLarge.SetTextViewText(Resource.Id.notification_large_body, $"SlowUdpPipe needs to keep an open port so that WireGuard can connect to it. " +
          $"This doesn't affect the battery life of the device, as SlowUdpPipe consumes very little energy when it is handling low background traffic." +
          $"\nUnfortunately, Android restricts the activity of background applications in order to save energy. " +
          $"To ensure the proper functioning of SlowUdpPipe, it is necessary to disable optimization. " +
          $"\nTo do so, go to SlowUdpPipe's settings -> search for battery related settings -> enable 'Unrestricted' mode " +
          $"(it may be also called 'Allow background activity')" +
          $"\nClick on this notification to open app's settings");

      var builder = new Notification.Builder(this, GENERAL_NOTIFICATION_CHANNEL)
       .SetContentIntent(notificationIntent)
       .SetSmallIcon(Resource.Drawable.infinity)
       .SetOnlyAlertOnce(true)
       .SetStyle(new Notification.DecoratedCustomViewStyle())
       .SetCustomContentView(layoutSmall)
       .SetCustomBigContentView(layoutLarge)
       .SetContentTitle("Battery optimization is enabled");

      var notification = builder.Build();
      p_notificationManager.Notify(BATTERY_OPTIMIZATION_NOTIFICATION_ID, notification);
      p_batteryOptimizationWarnDisplayed = true;
    }
  }

}
