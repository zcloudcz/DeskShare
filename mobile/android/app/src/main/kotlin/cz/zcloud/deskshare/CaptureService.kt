package cz.zcloud.deskshare

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.hardware.display.DisplayManager
import android.os.Handler
import android.os.Looper
import android.os.Build
import android.os.IBinder

/**
 * Foreground service of type mediaProjection. Android refuses to hand out screen frames
 * (and kills the capture in the background) without one. The capture itself runs in flutter_webrtc.
 */
class CaptureService : Service() {

    private var displayListener: DisplayManager.DisplayListener? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        displayListener?.let { getSystemService(DisplayManager::class.java).unregisterDisplayListener(it) }
        displayListener = null
        super.onDestroy()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val manager = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "Screen sharing", NotificationManager.IMPORTANCE_LOW)
            )
        }
        val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, CHANNEL_ID)
        } else {
            @Suppress("DEPRECATION") Notification.Builder(this)
        }
        val notification = builder
            .setContentTitle(getString(R.string.capture_notification_title))
            .setContentText(getString(R.string.capture_notification_text))
            .setSmallIcon(R.mipmap.ic_launcher)
            .setOngoing(true)
            .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
        if (displayListener == null) {
            // Runs while the app is in the background too, which is when the user rotates another app.
            val listener = object : DisplayManager.DisplayListener {
                override fun onDisplayAdded(displayId: Int) = Unit
                override fun onDisplayRemoved(displayId: Int) = Unit
                override fun onDisplayChanged(displayId: Int) {
                    CaptureService.onDisplayChanged?.invoke()
                }
            }
            getSystemService(DisplayManager::class.java).registerDisplayListener(listener, Handler(Looper.getMainLooper()))
            displayListener = listener
        }
        return START_NOT_STICKY
    }

    companion object {
        /** Set by MainActivity; forwards display size/rotation changes to Dart. */
        @Volatile
        var onDisplayChanged: (() -> Unit)? = null

        private const val CHANNEL_ID = "deskshare_sharing"
        private const val NOTIFICATION_ID = 4711
    }
}
