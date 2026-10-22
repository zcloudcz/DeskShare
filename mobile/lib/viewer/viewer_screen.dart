import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_webrtc/flutter_webrtc.dart';

import '../app.dart';
import '../core/input_message.dart';
import 'touch_mapper.dart';
import 'viewer_session.dart';

/// "Connect to computer": form -> remote screen with pinch-zoom/pan and optional touch control.
class ViewerScreen extends StatefulWidget {
  const ViewerScreen({super.key, required this.services});
  final AppServices services;

  @override
  State<ViewerScreen> createState() => _ViewerScreenState();
}

class _ViewerScreenState extends State<ViewerScreen> {
  late final ViewerSession _session = widget.services.createViewer();
  final _serverId = TextEditingController();
  final _passkey = TextEditingController();

  @override
  void dispose() {
    _session.dispose();
    _serverId.dispose();
    _passkey.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
        listenable: _session,
        builder: (context, _) {
          final connected = _session.state == ViewerState.connected;
          return Scaffold(
            appBar: connected ? null : AppBar(title: const Text('Connect to computer')),
            body: SafeArea(child: connected ? _remote(context) : _form(context)),
          );
        },
      );

  Widget _form(BuildContext context) {
    final theme = Theme.of(context);
    final busy = _session.state == ViewerState.connecting;
    final failed = _session.state == ViewerState.failed;
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        TextField(
          key: const Key('server-id-field'),
          controller: _serverId,
          enabled: !busy,
          keyboardType: TextInputType.number,
          inputFormatters: [FilteringTextInputFormatter.allow(RegExp(r'[0-9 \-a-zA-Z]'))],
          decoration: const InputDecoration(
            labelText: 'Server ID',
            hintText: '123 456 789',
            border: OutlineInputBorder(),
          ),
        ),
        const SizedBox(height: 16),
        TextField(
          key: const Key('passkey-field'),
          controller: _passkey,
          enabled: !busy,
          textCapitalization: TextCapitalization.characters,
          autocorrect: false,
          enableSuggestions: false,
          decoration: const InputDecoration(
            labelText: 'Passkey',
            hintText: 'XXX-XXX-XXX',
            helperText: 'Changes every 45 seconds',
            border: OutlineInputBorder(),
          ),
          onSubmitted: (_) => _connect(),
        ),
        const SizedBox(height: 16),
        FilledButton.icon(
          key: const Key('connect-button'),
          onPressed: busy ? null : _connect,
          icon: busy
              ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2))
              : const Icon(Icons.link),
          label: Text(busy ? 'Connecting...' : 'Connect'),
          style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
        ),
        const SizedBox(height: 12),
        if (_session.status.isNotEmpty)
          Text(
            _session.status,
            key: const Key('viewer-status'),
            textAlign: TextAlign.center,
            style: theme.textTheme.bodyMedium?.copyWith(color: failed ? theme.colorScheme.error : null),
          ),
      ],
    );
  }

  void _connect() {
    FocusScope.of(context).unfocus();
    _session.connect(serverId: _serverId.text, passkey: _passkey.text);
  }

  Widget _remote(BuildContext context) {
    final renderer = _session.renderer;
    return Column(
      children: [
        Expanded(
          child: Container(
            color: Colors.black,
            child: renderer == null
                ? const SizedBox.shrink()
                : _RemoteScreen(session: _session, renderer: renderer),
          ),
        ),
        _Toolbar(session: _session),
      ],
    );
  }
}

/// The video, letterboxed to its real aspect ratio so pointer positions map 1:1 to remote coordinates.
class _RemoteScreen extends StatefulWidget {
  const _RemoteScreen({required this.session, required this.renderer});
  final ViewerSession session;
  final RTCVideoRenderer renderer;

  @override
  State<_RemoteScreen> createState() => _RemoteScreenState();
}

class _RemoteScreenState extends State<_RemoteScreen> {
  final _mapper = TouchMapper();
  Size _size = Size.zero;

  void _emit(List<InputMessage> messages) {
    for (final m in messages) {
      widget.session.sendInput(m);
    }
  }

  @override
  void dispose() {
    _emit(_mapper.cancel());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => LayoutBuilder(builder: (context, box) {
        // Aspect ratio is known only after the first frame; fall back to 16:9.
        return ValueListenableBuilder<RTCVideoValue>(
          valueListenable: widget.renderer,
          builder: (context, value, _) {
            final aspect = value.aspectRatio > 0 ? value.aspectRatio : 16 / 9;
            var w = box.maxWidth;
            var h = w / aspect;
            if (h > box.maxHeight) {
              h = box.maxHeight;
              w = h * aspect;
            }
            _size = Size(w, h);
            final control = widget.session.controlEnabled;
            return Center(
              child: SizedBox(
                width: w,
                height: h,
                child: InteractiveViewer(
                  // While controlling, one/two-finger gestures belong to the remote mouse.
                  panEnabled: !control,
                  scaleEnabled: !control,
                  maxScale: 6,
                  child: Listener(
                    onPointerDown: control ? (e) => _emit(_mapper.down(e.pointer, e.localPosition, _size)) : null,
                    onPointerMove: control ? (e) => _emit(_mapper.move(e.pointer, e.localPosition, _size)) : null,
                    onPointerUp: control ? (e) => _emit(_mapper.up(e.pointer, e.localPosition, _size)) : null,
                    onPointerCancel: control ? (_) => _emit(_mapper.cancel()) : null,
                    child: RTCVideoView(widget.renderer, objectFit: RTCVideoViewObjectFit.RTCVideoViewObjectFitContain),
                  ),
                ),
              ),
            );
          },
        );
      });
}

class _Toolbar extends StatefulWidget {
  const _Toolbar({required this.session});
  final ViewerSession session;

  @override
  State<_Toolbar> createState() => _ToolbarState();
}

class _ToolbarState extends State<_Toolbar> {
  bool _keyboard = false;
  final _text = TextEditingController();
  final _focus = FocusNode();
  String _previous = '';

  @override
  void dispose() {
    _text.dispose();
    _focus.dispose();
    super.dispose();
  }

  void _key(int vk) {
    for (final m in pressKey(vk)) {
      widget.session.sendInput(m);
    }
  }

  /// Sends what changed in the text field as key presses (append at the end, or backspaces).
  void _onText(String value) {
    if (value.length > _previous.length && value.startsWith(_previous)) {
      for (final ch in value.substring(_previous.length).split('')) {
        for (final m in typeChar(ch)) {
          widget.session.sendInput(m);
        }
      }
    } else if (value.length < _previous.length) {
      for (var i = 0; i < _previous.length - value.length; i++) {
        _key(8);
      }
    }
    _previous = value;
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.session;
    final theme = Theme.of(context);
    return Material(
      color: theme.colorScheme.surfaceContainerHigh,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (_keyboard && s.controlEnabled)
            Padding(
              padding: const EdgeInsets.fromLTRB(8, 8, 8, 0),
              child: Column(
                children: [
                  TextField(
                    key: const Key('keyboard-field'),
                    controller: _text,
                    focusNode: _focus,
                    autofocus: true,
                    autocorrect: false,
                    enableSuggestions: false,
                    textInputAction: TextInputAction.send,
                    decoration: const InputDecoration(
                      isDense: true,
                      hintText: 'Type here to send keys',
                      border: OutlineInputBorder(),
                    ),
                    onChanged: _onText,
                    onSubmitted: (_) {
                      _key(13);
                      _focus.requestFocus();
                    },
                  ),
                  Wrap(
                    spacing: 4,
                    children: [
                      for (final k in const {'Esc': 27, 'Tab': 9, 'Left': 37, 'Up': 38, 'Down': 40, 'Right': 39}.entries)
                        TextButton(onPressed: () => _key(k.value), child: Text(k.key)),
                    ],
                  ),
                ],
              ),
            ),
          Padding(
            padding: const EdgeInsets.all(8),
            child: Row(
              children: [
                Expanded(
                  child: s.controlEnabled
                      ? FilledButton.tonalIcon(
                          key: const Key('control-button'),
                          onPressed: () => s.setControl(false),
                          icon: const Icon(Icons.touch_app),
                          label: const Text('Disable control'),
                        )
                      : OutlinedButton.icon(
                          key: const Key('control-button'),
                          onPressed: s.controlAvailable ? () => s.setControl(true) : null,
                          icon: const Icon(Icons.touch_app_outlined),
                          label: Text(s.controlAvailable ? 'Enable control' : 'View only'),
                        ),
                ),
                IconButton(
                  key: const Key('keyboard-button'),
                  tooltip: 'Keyboard',
                  onPressed: s.controlEnabled ? () => setState(() => _keyboard = !_keyboard) : null,
                  icon: Icon(_keyboard ? Icons.keyboard_hide : Icons.keyboard),
                ),
                IconButton(
                  key: const Key('disconnect-button'),
                  tooltip: 'Disconnect',
                  onPressed: () async {
                    final navigator = Navigator.of(context);
                    await s.disconnect();
                    navigator.maybePop();
                  },
                  icon: const Icon(Icons.close),
                ),
              ],
            ),
          ),
          if (!s.controlEnabled)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: Text(
                s.status.isEmpty ? 'Pinch to zoom, drag to pan' : '${s.status}. Pinch to zoom, drag to pan',
                style: theme.textTheme.bodySmall,
                textAlign: TextAlign.center,
              ),
            ),
        ],
      ),
    );
  }
}
