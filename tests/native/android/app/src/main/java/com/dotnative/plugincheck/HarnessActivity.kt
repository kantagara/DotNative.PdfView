package com.dotnative.plugincheck
import android.app.Activity
import android.os.Bundle
import com.dotnative.plugins.*
class HarnessActivity : Activity() {
 override fun onCreate(state: Bundle?) {
  super.onCreate(state)
  setContentView(android.widget.TextView(this).apply { text = "Native PDF / Ads validation" })
  PdfViewPlugin(this)
 }
 fun makePdf(): String {
  val file = java.io.File(cacheDir,"test.pdf")
  val doc = android.graphics.pdf.PdfDocument()
  for (n in 1..2) {
   val page = doc.startPage(android.graphics.pdf.PdfDocument.PageInfo.Builder(400,600,n).create())
   page.canvas.drawText("PDF PAGE $n",40f,100f,android.graphics.Paint().apply { textSize=32f })
   doc.finishPage(page)
  }
  file.outputStream().use { doc.writeTo(it) }; doc.close(); return file.path
 }
}
