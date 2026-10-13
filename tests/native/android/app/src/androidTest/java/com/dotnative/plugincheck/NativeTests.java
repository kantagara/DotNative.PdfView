package com.dotnative.plugincheck;
import android.test.ActivityInstrumentationTestCase2;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;
import com.dotnative.plugins.*;
import java.util.*;
import java.util.concurrent.TimeUnit;
public class NativeTests extends ActivityInstrumentationTestCase2<HarnessActivity> {
 public NativeTests(){super(HarnessActivity.class);}
 private PluginReply call(String channel,String method,Map<String,Object> args) {
  PluginReply reply=new PluginReply();
  getInstrumentation().runOnMainSync(()->NativeChannels.INSTANCE.channel(channel).call(method,args,reply));
  return reply;
 }
 private String tree(android.view.accessibility.AccessibilityNodeInfo node) {String s="["+node.getText()+"/"+node.getContentDescription()+"]";for(int i=0;i<node.getChildCount();i++){var c=node.getChild(i);if(c!=null)s+=tree(c);}return s;}
 private boolean clickNext(android.view.accessibility.AccessibilityNodeInfo node) {
  if ("Next page".contentEquals(node.getContentDescription()==null?"":node.getContentDescription()))
   return node.performAction(android.view.accessibility.AccessibilityNodeInfo.ACTION_CLICK);
  for(int i=0;i<node.getChildCount();i++){android.view.accessibility.AccessibilityNodeInfo child=node.getChild(i);if(child!=null && clickNext(child))return true;}
  return false;
 }
 public void testPdfRenderNavigateAndCancel() throws Exception {
  getInstrumentation().getUiAutomation(); Thread.sleep(300);
  HarnessActivity activity=getActivity(); final String[] path=new String[1];
  getInstrumentation().runOnMainSync(()->path[0]=activity.makePdf());
  Map<String,Object> args=new HashMap<>();args.put("path",path[0]);args.put("title","Native PDF Test");args.put("initialPage",0);
  PluginReply reply=call("dotnative.pdf-view","present",args);
  Thread.sleep(900); assertNull(reply.getError());assertEquals(1L,reply.getDone().getCount());
  android.view.accessibility.AccessibilityNodeInfo root=null;
  for(int i=0;i<100;i++){root=getInstrumentation().getUiAutomation().getRootInActiveWindow();if(root!=null&&!root.findAccessibilityNodeInfosByText("1 / 2").isEmpty())break;Thread.sleep(100);}
  assertFalse(tree(root), root.findAccessibilityNodeInfosByText("1 / 2").isEmpty());
  assertTrue(clickNext(root));Thread.sleep(500);
  root=getInstrumentation().getUiAutomation().getRootInActiveWindow();
  assertFalse(root.findAccessibilityNodeInfosByText("2 / 2").isEmpty());
  getInstrumentation().runOnMainSync(reply::cancel);
  Thread.sleep(300);
  // The OS may evict cache files under disk pressure. Managed PdfCache redownloads on reopen;
  // recreate the local native fixture at the same boundary.
  getInstrumentation().runOnMainSync(()->activity.makePdf());
  PluginReply second=call("dotnative.pdf-view","present",args);Thread.sleep(500);
  assertNull(second.getMessage(), second.getError());assertEquals(1L,second.getDone().getCount());
  getInstrumentation().runOnMainSync(second::cancel);Thread.sleep(300);
  getInstrumentation().runOnMainSync(()->activity.makePdf());
  args.put("initialPage",99);PluginReply bad=call("dotnative.pdf-view","present",args);
  assertTrue(bad.getDone().await(3,TimeUnit.SECONDS));assertEquals("invalid_pdf",bad.getError());
 }
}
