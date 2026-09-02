package com.smatechnology.denimrolls;

import android.app.Application;

import com.google.android.material.color.DynamicColors;

/**
 * Application entry point.
 *
 * <p>Deliberately thin. The reader is owned by the screen that uses it so the
 * UHF module is released the moment the operator leaves the scan screen,
 * rather than being held for the life of the process on a device that may stay
 * powered for months.
 */
public final class DenimRollsApp extends Application {

    @Override
    public void onCreate() {
        super.onCreate();

        // Honour the device palette where the OEM provides one, without
        // losing the brand colour as the primary.
        DynamicColors.applyToActivitiesIfAvailable(this);
    }
}
