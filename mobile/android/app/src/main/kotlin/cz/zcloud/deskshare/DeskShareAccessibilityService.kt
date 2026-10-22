package cz.zcloud.deskshare

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.view.accessibility.AccessibilityEvent

/**
 * Injects the taps/swipes and Back/Home/Recents that a remote viewer sends.
 * It declares no event types and no window-content access (see accessibility_service_config.xml).
 */
class DeskShareAccessibilityService : AccessibilityService() {

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
    }

    override fun onUnbind(intent: android.content.Intent?): Boolean {
        if (instance === this) instance = null
        return super.onUnbind(intent)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) = Unit

    override fun onInterrupt() = Unit

    /** One finger stroke along [points] (screen pixels) in [durationMs]; a single point is a tap/long press. */
    fun stroke(points: List<Pair<Float, Float>>, durationMs: Long, done: (Boolean) -> Unit) {
        if (points.isEmpty()) return done(false)
        val path = Path().apply {
            moveTo(points[0].first, points[0].second)
            for (i in 1 until points.size) lineTo(points[i].first, points[i].second)
        }
        val gesture = GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0, durationMs.coerceAtLeast(1)))
            .build()
        val accepted = dispatchGesture(gesture, object : GestureResultCallback() {
            override fun onCompleted(g: GestureDescription?) = done(true)
            override fun onCancelled(g: GestureDescription?) = done(false)
        }, null)
        if (!accepted) done(false)
    }

    fun global(name: String): Boolean = when (name) {
        "back" -> performGlobalAction(GLOBAL_ACTION_BACK)
        "home" -> performGlobalAction(GLOBAL_ACTION_HOME)
        "recents" -> performGlobalAction(GLOBAL_ACTION_RECENTS)
        else -> false
    }

    companion object {
        @Volatile
        var instance: DeskShareAccessibilityService? = null
    }
}
