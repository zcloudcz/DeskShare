package cz.zcloud.deskshare

import android.content.ComponentName
import android.content.Intent
import android.os.Build
import android.provider.Settings
import android.os.Handler
import android.os.Looper
import com.cloudwebrtc.webrtc.GetUserMediaImpl
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodCall
import io.flutter.plugin.common.MethodChannel

/** Native side of lib/sender/device_control.dart. */
class MainActivity : FlutterActivity() {

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        val channel = MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "cz.zcloud.deskshare/device")
        channel.setMethodCallHandler { call, result -> handle(call, result) }

        // The user can end screen capture from the system UI: Dart must revoke remote control right away.
        // (Upstream flutter_webrtc ignores MediaProjection.onStop; see third_party/flutter_webrtc/DESKSHARE_PATCH.md.)
        val main = Handler(Looper.getMainLooper())
        GetUserMediaImpl.projectionStoppedListener = Runnable {
            main.post { channel.invokeMethod("projectionStopped", null) }
        }
        // Rotation / folding changes the pixel space of gestures.
        CaptureService.onDisplayChanged = { main.post { channel.invokeMethod("displayChanged", null) } }
    }

    override fun cleanUpFlutterEngine(flutterEngine: FlutterEngine) {
        GetUserMediaImpl.projectionStoppedListener = null
        CaptureService.onDisplayChanged = null
        super.cleanUpFlutterEngine(flutterEngine)
    }

    private fun handle(call: MethodCall, result: MethodChannel.Result) {
        when (call.method) {
            "isAccessibilityEnabled" -> result.success(isAccessibilityEnabled())
            "openAccessibilitySettings" -> {
                startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                result.success(null)
            }
            "displaySize" -> {
                val (w, h) = realDisplaySize()
                result.success(mapOf("width" to w, "height" to h))
            }
            "stroke" -> {
                val service = DeskShareAccessibilityService.instance
                if (service == null) {
                    result.error("no_service", "Accessibility service is not enabled", null)
                    return
                }
                val points = (call.argument<List<List<Double>>>("points") ?: emptyList())
                    .map { Pair(it[0].toFloat(), it[1].toFloat()) }
                val duration = (call.argument<Number>("durationMs") ?: 50).toLong()
                service.stroke(points, duration) { ok -> result.success(ok) }
            }
            "globalAction" -> {
                val service = DeskShareAccessibilityService.instance
                result.success(service?.global(call.argument<String>("name") ?: "") ?: false)
            }
            "startCaptureService" -> {
                val intent = Intent(this, CaptureService::class.java)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(intent) else startService(intent)
                result.success(null)
            }
            "stopCaptureService" -> {
                stopService(Intent(this, CaptureService::class.java))
                result.success(null)
            }
            else -> result.notImplemented()
        }
    }

    /** True when our accessibility service is switched on in system settings. */
    private fun isAccessibilityEnabled(): Boolean {
        val enabled = Settings.Secure.getString(contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
            ?: return false
        val me = ComponentName(this, DeskShareAccessibilityService::class.java).flattenToString()
        return enabled.split(':').any { it.equals(me, ignoreCase = true) }
    }

    /** Real display size in pixels: the coordinate space of dispatchGesture. */
    private fun realDisplaySize(): Pair<Double, Double> {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val b = windowManager.maximumWindowMetrics.bounds
            return Pair(b.width().toDouble(), b.height().toDouble())
        }
        val m = android.util.DisplayMetrics()
        @Suppress("DEPRECATION") windowManager.defaultDisplay.getRealMetrics(m)
        return Pair(m.widthPixels.toDouble(), m.heightPixels.toDouble())
    }
}
