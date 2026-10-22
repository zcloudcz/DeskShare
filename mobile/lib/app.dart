import 'dart:io' show Platform;

import 'package:flutter/material.dart';

import 'core/settings.dart';
import 'sender/device_control.dart';
import 'sender/sender_screen.dart';
import 'sender/sender_session.dart';
import 'settings_screen.dart';
import 'viewer/viewer_screen.dart';
import 'viewer/viewer_session.dart';

/// Everything the screens need from the outside world; tests pass fakes.
class AppServices {
  AppServices({
    required this.settings,
    required this.device,
    required this.canShare,
    ViewerSession Function()? createViewer,
    SenderSession Function()? createSender,
  })  : createViewer = createViewer ?? (() => WebRtcViewerSession(signalingUrl: settings.signalingUrl)),
        createSender = createSender ?? (() => WebRtcSenderSession(settings: settings, device: device));

  final AppSettings settings;
  final DeviceControl device;

  /// Sharing this phone's screen: Android only (iOS needs a Broadcast Extension, not built yet).
  final bool canShare;
  final ViewerSession Function() createViewer;
  final SenderSession Function() createSender;

  static Future<AppServices> real() async => AppServices(
        settings: await AppSettings.load(),
        device: MethodChannelDeviceControl(),
        canShare: Platform.isAndroid,
      );
}

class DeskShareApp extends StatelessWidget {
  const DeskShareApp({super.key, required this.services});
  final AppServices services;

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'DeskShare',
        debugShowCheckedModeBanner: false,
        theme: ThemeData(
          useMaterial3: true,
          colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF2563EB)),
        ),
        home: HomeScreen(services: services),
      );
}

class HomeScreen extends StatelessWidget {
  const HomeScreen({super.key, required this.services});
  final AppServices services;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      appBar: AppBar(
        title: const Text('DeskShare'),
        actions: [
          IconButton(
            tooltip: 'Settings',
            icon: const Icon(Icons.settings_outlined),
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(builder: (_) => SettingsScreen(settings: services.settings)),
            ),
          ),
        ],
      ),
      body: SafeArea(
        child: ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Text('What do you want to do?', style: theme.textTheme.titleMedium),
            const SizedBox(height: 16),
            _ModeCard(
              key: const Key('mode-share'),
              icon: Icons.phone_android,
              title: 'Share this phone',
              subtitle: services.canShare
                  ? 'Show this screen to someone else. Optionally let them control it.'
                  : 'Not available on iPhone yet. iOS sharing needs a Broadcast Extension, and Apple does not allow remote control.',
              enabled: services.canShare,
              onTap: () => Navigator.of(context).push(
                MaterialPageRoute<void>(builder: (_) => SenderScreen(services: services)),
              ),
            ),
            const SizedBox(height: 12),
            _ModeCard(
              key: const Key('mode-connect'),
              icon: Icons.desktop_windows_outlined,
              title: 'Connect to computer',
              subtitle: 'Enter the Server ID and passkey shown by DeskShare on the other device.',
              enabled: true,
              onTap: () => Navigator.of(context).push(
                MaterialPageRoute<void>(builder: (_) => ViewerScreen(services: services)),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ModeCard extends StatelessWidget {
  const _ModeCard({
    super.key,
    required this.icon,
    required this.title,
    required this.subtitle,
    required this.enabled,
    required this.onTap,
  });

  final IconData icon;
  final String title;
  final String subtitle;
  final bool enabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final fg = enabled ? theme.colorScheme.onSurface : theme.colorScheme.onSurface.withValues(alpha: 0.5);
    return Card(
      child: InkWell(
        borderRadius: BorderRadius.circular(12),
        onTap: enabled ? onTap : null,
        child: Padding(
          padding: const EdgeInsets.all(20),
          child: Row(
            children: [
              Icon(icon, size: 40, color: enabled ? theme.colorScheme.primary : fg),
              const SizedBox(width: 16),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(title, style: theme.textTheme.titleLarge?.copyWith(color: fg)),
                    const SizedBox(height: 4),
                    Text(subtitle, style: theme.textTheme.bodyMedium?.copyWith(color: fg)),
                  ],
                ),
              ),
              if (enabled) const Icon(Icons.chevron_right),
            ],
          ),
        ),
      ),
    );
  }
}
