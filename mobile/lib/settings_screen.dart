import 'package:flutter/material.dart';

import 'core/settings.dart';
import 'core/signaling_api.dart';

/// Signaling server address: the public one by default, changeable for self-hosting and tests.
class SettingsScreen extends StatefulWidget {
  const SettingsScreen({super.key, required this.settings});
  final AppSettings settings;

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen> {
  late final TextEditingController _url = TextEditingController(text: widget.settings.signalingUrl);
  String? _error;

  @override
  void dispose() {
    _url.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final text = _url.text.trim();
    final uri = Uri.tryParse(text);
    if (text.isNotEmpty && (uri == null || !{'ws', 'wss'}.contains(uri.scheme) || uri.host.isEmpty)) {
      setState(() => _error = 'Use a ws:// or wss:// address, e.g. $defaultSignalingUrl');
      return;
    }
    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);
    await widget.settings.setSignalingUrl(text);
    messenger.showSnackBar(const SnackBar(content: Text('Saved. Applies to the next connection.')));
    navigator.pop();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: const Text('Settings')),
        body: SafeArea(
          child: ListView(
            padding: const EdgeInsets.all(16),
            children: [
              TextField(
                key: const Key('signaling-url'),
                controller: _url,
                keyboardType: TextInputType.url,
                autocorrect: false,
                decoration: InputDecoration(
                  labelText: 'Signaling server',
                  helperText: 'Default: $defaultSignalingUrl',
                  errorText: _error,
                  border: const OutlineInputBorder(),
                ),
              ),
              const SizedBox(height: 16),
              Row(
                children: [
                  OutlinedButton(
                    onPressed: () => setState(() {
                      _url.text = defaultSignalingUrl;
                      _error = null;
                    }),
                    child: const Text('Reset'),
                  ),
                  const SizedBox(width: 12),
                  FilledButton(onPressed: _save, child: const Text('Save')),
                ],
              ),
            ],
          ),
        ),
      );
}
