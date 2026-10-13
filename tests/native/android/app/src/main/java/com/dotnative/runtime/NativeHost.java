package com.dotnative.runtime;
import android.view.View;
public class NativeHost {
 public interface PlatformViewFactory { View create(int id); default void detach(View v,int id){} default void rebind(View v,int id){} }
 public static void registerPlatformView(String name,PlatformViewFactory factory){}
}
