import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../app.dart';
import '../core/ids.dart';
import 'sender_session.dart';

/// "Share this phone": Server ID, rotating passkey, remote-control toggle, Start/Stop.
class SenderScreen extends StatefulWidget {
  const SenderScreen({super.key, required this.services});
  final AppServices services;

  @override
  State<SenderScreen> createState() => _SenderScreenState();
}

class _SenderScreenState extends State<SenderScreen> with WidgetsBindingObserver {
  late final SenderSession _session = widget.services.createSender();
  bool _accessibilityOn = false;
  Timer? _tick;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _refreshAccessibility();
    // Drives the passkey countdown.
    _tick = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted && _session.state == SenderState.sharing) setState(() {});
    });
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _tick?.cancel();
    _session.dispose(); // also stops sharing
    super.dispose();
  }

  /// The user comes back from the system Accessibility settings: re-check.
  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) _refreshAccessibility();
  }

  Future<void> _refreshAccessibility() async {
    final on = await widget.services.device.isAccessibilityEnabled();
    if (mounted) setState(() => _accessibilityOn = on);
  }

  Future<void> _onControlToggle(bool value) async {
    _session.controlRequested = value;
    if (value && !_accessibilityOn) await _explainAccessibility();
  }

  Future<void> _explainAccessibility() async {
    final open = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Accessibility permission needed'),
        content: const Text(
          'To let someone tap and swipe on this phone, DeskShare uses an Android Accessibility service. '
          'It is only used to perform the taps and swipes that the connected viewer sends, and only while '
          'you share with remote control on. It does not read your screen content or typed text.\n\n'
          'Turn on "DeskShare remote control" in the next screen.',
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(ctx, false), child: const Text('Not now')),
          FilledButton(onPressed: () => Navigator.pop(ctx, true), child: const Text('Open settings')),
        ],
      ),
    );
    if (open == true) await widget.services.device.openAccessibilitySettings();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: const Text('Share this phone')),
        body: SafeArea(
          child: ListenableBuilder(
            listenable: _session,
            builder: (context, _) => _body(context),
          ),
        ),
      );

  Widget _body(BuildContext context) {
    final theme = Theme.of(context);
    final sharing = _session.state == SenderState.sharing;
    final busy = _session.state == SenderState.starting;
    final failed = _session.state == SenderState.failed;
    final needsAccessibility = _session.controlRequested && !_accessibilityOn;

    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        _CodeCard(
          label: 'Server ID',
          value: formatServerId(_session.serverId),
          valueKey: const Key('server-id'),
          copyValue: _session.serverId.replaceFirst(serverIdPrefix, ''),
        ),
        const SizedBox(height: 12),
        _CodeCard(
          label: 'Passkey',
          value: _session.passkey.isEmpty ? '---' : formatPasskey(_session.passkey),
          valueKey: const Key('passkey'),
          copyValue: _session.passkey.isEmpty ? null : _session.passkey,
          footer: sharing ? _countdown() : 'Start sharing to get a passkey. It changes every $passkeyValiditySeconds seconds.',
        ),
        const SizedBox(height: 12),
        Card(
          child: SwitchListTile(
            key: const Key('control-switch'),
            title: const Text('Allow remote control'),
            subtitle: const Text('The viewer can tap, swipe and scroll on this phone. Off: view only.'),
            value: _session.controlRequested,
            onChanged: sharing || busy ? null : _onControlToggle,
          ),
        ),
        if (needsAccessibility)
          Card(
            key: const Key('accessibility-warning'),
            color: theme.colorScheme.tertiaryContainer,
            child: ListTile(
              leading: const Icon(Icons.accessibility_new),
              title: const Text('Accessibility permission is off'),
              subtitle: const Text('Without it, viewers can only watch. Tap to turn it on.'),
              onTap: _explainAccessibility,
            ),
          ),
        const SizedBox(height: 16),
        FilledButton.icon(
          key: const Key('start-stop'),
          onPressed: busy ? null : (sharing ? _session.stop : _session.start),
          icon: busy
              ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2))
              : Icon(sharing ? Icons.stop : Icons.play_arrow),
          label: Text(sharing ? 'Stop sharing' : 'Start sharing'),
          style: FilledButton.styleFrom(
            minimumSize: const Size.fromHeight(52),
            backgroundColor: sharing ? theme.colorScheme.error : null,
          ),
        ),
        const SizedBox(height: 12),
        if (_session.status.isNotEmpty)
          Text(
            _session.status,
            key: const Key('sender-status'),
            textAlign: TextAlign.center,
            style: theme.textTheme.bodyMedium?.copyWith(color: failed ? theme.colorScheme.error : null),
          ),
      ],
    );
  }

  String _countdown() {
    final validTo = _session.passkeyValidTo;
    if (validTo == null) return '';
    final left = validTo.difference(DateTime.now().toUtc()).inSeconds.clamp(0, passkeyValiditySeconds);
    return 'New passkey in ${left}s';
  }
}

class _CodeCard extends StatelessWidget {
  const _CodeCard({required this.label, required this.value, required this.valueKey, this.copyValue, this.footer});

  final String label;
  final String value;
  final Key valueKey;
  final String? copyValue;
  final String? footer;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(20, 16, 8, 16),
        child: Row(
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(label, style: theme.textTheme.labelLarge),
                  const SizedBox(height: 4),
                  Text(
                    value,
                    key: valueKey,
                    style: theme.textTheme.headlineMedium?.copyWith(
                      fontFeatures: const [FontFeature.tabularFigures()],
                      letterSpacing: 2,
                    ),
                  ),
                  if (footer != null) ...[
                    const SizedBox(height: 4),
                    Text(footer!, style: theme.textTheme.bodySmall),
                  ],
                ],
              ),
            ),
            IconButton(
              tooltip: 'Copy $label',
              icon: const Icon(Icons.copy),
              onPressed: copyValue == null
                  ? null
                  : () {
                      Clipboard.setData(ClipboardData(text: copyValue!));
                      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('$label copied')));
                    },
            ),
          ],
        ),
      ),
    );
  }
}
