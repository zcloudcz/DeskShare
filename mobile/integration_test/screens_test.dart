// Parks on each screen for a while so a screenshot can be taken with `adb exec-out screencap -p`.
import 'package:deskshare/app.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import 'e2e_test.dart' show servicesFor;

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('park on home, sender and viewer screens', (tester) async {
    await tester.pumpWidget(DeskShareApp(services: await servicesFor()));
    await tester.pumpAndSettle();
    await tester.runAsync(() => Future<void>.delayed(const Duration(seconds: 12))); // screenshot: home

    await tester.tap(find.byKey(const Key('mode-share')));
    await tester.pumpAndSettle();
    await tester.runAsync(() => Future<void>.delayed(const Duration(seconds: 12))); // screenshot: share

    await tester.pageBack();
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('mode-connect')));
    await tester.pumpAndSettle();
    await tester.runAsync(() => Future<void>.delayed(const Duration(seconds: 12))); // screenshot: connect
  });
}
