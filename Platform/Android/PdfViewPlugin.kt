package com.dotnative.plugins

import android.app.Activity
import android.app.Dialog
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.pdf.PdfRenderer
import android.os.ParcelFileDescriptor
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import java.io.File
import java.util.concurrent.Executors

class PdfViewPlugin(private val activity: Activity) {
    private var session: Session? = null

    init {
        NativeChannels.channel("dotnative.pdf-view").handle("cacheDirectory") { _, reply ->
            reply.success(File(activity.cacheDir, "dotnative-pdf-v1").absolutePath)
        }
        NativeChannels.channel("dotnative.pdf-view").handle("present") { args, reply ->
            val fields = args as? Map<*, *>
            val path = fields?.get("path") as? String
            val title = fields?.get("title") as? String
            val page = (fields?.get("initialPage") as? Number)?.toInt() ?: 0
            if (session != null) {
                reply.failure("busy", "A PDF viewer is already open")
                return@handle
            }
            if (path == null || title == null || page < 0) {
                reply.failure("invalid_argument", "PDF path/title/page required")
                return@handle
            }
            val file = File(path).canonicalFile
            if (
                !file.path.startsWith(activity.cacheDir.canonicalPath + File.separator) &&
                    !file.path.startsWith(
                        File(System.getProperty("java.io.tmpdir") ?: activity.cacheDir.path)
                            .canonicalPath + File.separator
                    )
            ) {
                reply.failure("invalid_argument", "PDF file must be in the private cache")
                return@handle
            }
            val current =
                Session(
                    file,
                    title,
                    page,
                    reply,
                    fields?.get("closeLabel") as? String ?: "Close",
                    fields?.get("previousLabel") as? String ?: "Previous page",
                    fields?.get("nextLabel") as? String ?: "Next page",
                )
            session = current
            reply.onCancel = { current.close(false) }
            current.open()
        }
        activity.application.registerActivityLifecycleCallbacks(
            object : android.app.Application.ActivityLifecycleCallbacks {
                override fun onActivityDestroyed(a: Activity) {
                    if (a === activity) {
                        session?.close(false)
                        activity.application.unregisterActivityLifecycleCallbacks(this)
                    }
                }

                override fun onActivityStopped(a: Activity) {
                    if (a === activity && !a.isChangingConfigurations) session?.close(false)
                }

                override fun onActivityCreated(a: Activity, b: android.os.Bundle?) {}

                override fun onActivityStarted(a: Activity) {}

                override fun onActivityResumed(a: Activity) {}

                override fun onActivityPaused(a: Activity) {}

                override fun onActivitySaveInstanceState(a: Activity, b: android.os.Bundle) {}
            }
        )
    }

    private inner class Session(
        val file: File,
        val title: String,
        var index: Int,
        val reply: PluginReply,
        val closeLabel: String,
        val previousLabel: String,
        val nextLabel: String,
    ) {
        val worker = Executors.newSingleThreadExecutor()
        val dialog = Dialog(activity, android.R.style.Theme_Material_Light_NoActionBar)
        val label = TextView(activity)
        val spinner = ProgressBar(activity)
        val image = PdfPage(activity) { step -> change(step) }
        var renderer: PdfRenderer? = null
        var descriptor: ParcelFileDescriptor? = null
        var count = 0
        var closed = false
        var generation = 0

        fun open() {
            val root =
                LinearLayout(activity).apply {
                    orientation = LinearLayout.VERTICAL
                    setBackgroundColor(Color.WHITE)
                }
            val toolbar = LinearLayout(activity)
            toolbar.addView(
                Button(activity).apply {
                    text = "×"
                    contentDescription = closeLabel
                    setOnClickListener { close(true) }
                }
            )
            toolbar.addView(
                TextView(activity).apply {
                    text = title
                    maxLines = 2
                },
                LinearLayout.LayoutParams(0, -2, 1f),
            )
            root.addView(toolbar)
            root.addView(spinner)
            root.addView(image, LinearLayout.LayoutParams(-1, 0, 1f))
            val navigation = LinearLayout(activity)
            navigation.addView(
                Button(activity).apply {
                    text = "‹"
                    contentDescription = previousLabel
                    setOnClickListener { change(-1) }
                }
            )
            navigation.addView(label, LinearLayout.LayoutParams(0, -2, 1f))
            navigation.addView(
                Button(activity).apply {
                    text = "›"
                    contentDescription = nextLabel
                    setOnClickListener { change(1) }
                }
            )
            root.addView(navigation)
            dialog.setContentView(root)
            dialog.setOnCancelListener { close(true) }
            dialog.show()
            dialog.window?.setLayout(-1, -1)
            worker.execute {
                try {
                    descriptor =
                        ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY)
                    renderer = PdfRenderer(descriptor!!)
                    val pages = renderer!!.pageCount
                    if (index >= pages)
                        throw IllegalArgumentException("Initial page is outside the document")
                    activity.runOnUiThread {
                        if (!closed) {
                            count = pages
                            render()
                        }
                    }
                } catch (error: Exception) {
                    fail(error)
                }
            }
        }

        fun change(step: Int) {
            if (closed || count == 0) return
            val next = (index + step).coerceIn(0, count - 1)
            if (next != index) {
                index = next
                render()
            }
        }

        fun render() {
            val version = ++generation
            val pageIndex = index
            spinner.visibility = View.VISIBLE
            label.text = "${index + 1} / $count"
            worker.execute {
                try {
                    val bitmap =
                        renderer!!.openPage(pageIndex).use { page ->
                            // Bounded bitmap, zoom/pan happens in the native view; no full-document
                            // bitmap allocation.
                            val scale = minOf(2f, 2048f / maxOf(page.width, page.height))
                            Bitmap.createBitmap(
                                    maxOf(1, (page.width * scale).toInt()),
                                    maxOf(1, (page.height * scale).toInt()),
                                    Bitmap.Config.ARGB_8888,
                                )
                                .also {
                                    it.eraseColor(Color.WHITE)
                                    page.render(
                                        it,
                                        null,
                                        null,
                                        PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY,
                                    )
                                }
                        }
                    activity.runOnUiThread {
                        if (closed || version != generation) bitmap.recycle()
                        else {
                            image.setPage(bitmap)
                            spinner.visibility = View.GONE
                        }
                    }
                } catch (error: Exception) {
                    fail(error)
                }
            }
        }

        fun fail(error: Exception) =
            activity.runOnUiThread {
                if (!closed) {
                    reply.failure("invalid_pdf", error.message ?: "The PDF could not be rendered")
                    close(false)
                }
            }

        fun close(success: Boolean) {
            if (closed) return
            closed = true
            generation++
            dialog.setOnCancelListener(null)
            dialog.dismiss()
            image.clear()
            worker.execute {
                runCatching { renderer?.close() }
                runCatching { descriptor?.close() }
                activity.runOnUiThread {
                    if (session === this) session = null
                    if (success) reply.success()
                    else reply.failure("cancelled", "PDF viewer closed")
                }
            }
            worker.shutdown()
        }
    }
}

private class PdfPage(activity: Activity, val turn: (Int) -> Unit) : View(activity) {
    private var bitmap: Bitmap? = null
    private var zoom = 1f
    private var offsetX = 0f
    private var offsetY = 0f
    private val paint = Paint(Paint.FILTER_BITMAP_FLAG)
    private val scale =
        ScaleGestureDetector(
            activity,
            object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
                override fun onScale(detector: ScaleGestureDetector): Boolean {
                    zoom = (zoom * detector.scaleFactor).coerceIn(1f, 6f)
                    invalidate()
                    return true
                }
            },
        )
    private val gesture =
        GestureDetector(
            activity,
            object : GestureDetector.SimpleOnGestureListener() {
                override fun onDown(event: MotionEvent) = true

                override fun onDoubleTap(event: MotionEvent): Boolean {
                    zoom = if (zoom > 1f) 1f else 2f
                    offsetX = 0f
                    offsetY = 0f
                    invalidate()
                    return true
                }

                override fun onScroll(
                    first: MotionEvent?,
                    current: MotionEvent,
                    dx: Float,
                    dy: Float,
                ): Boolean {
                    if (zoom > 1f) {
                        offsetX -= dx
                        offsetY -= dy
                        invalidate()
                    }
                    return true
                }

                override fun onFling(
                    first: MotionEvent?,
                    current: MotionEvent,
                    vx: Float,
                    vy: Float,
                ): Boolean {
                    if (zoom == 1f && first != null && kotlin.math.abs(current.y - first.y) > 60)
                        turn(if (vy < 0) 1 else -1)
                    return true
                }
            },
        )

    fun setPage(value: Bitmap) {
        bitmap?.recycle()
        bitmap = value
        zoom = 1f
        offsetX = 0f
        offsetY = 0f
        invalidate()
    }

    fun clear() {
        bitmap?.recycle()
        bitmap = null
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val page = bitmap ?: return
        val factor = minOf(width.toFloat() / page.width, height.toFloat() / page.height) * zoom
        canvas.save()
        canvas.translate(
            (width - page.width * factor) / 2 + offsetX,
            (height - page.height * factor) / 2 + offsetY,
        )
        canvas.scale(factor, factor)
        canvas.drawBitmap(page, 0f, 0f, paint)
        canvas.restore()
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        scale.onTouchEvent(event)
        gesture.onTouchEvent(event)
        return true
    }
}
