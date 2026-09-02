package com.smatechnology.denimrolls.data;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;
import android.text.TextUtils;

/**
 * Everything the app needs to know about its deployment, held on the device.
 *
 * <p>Nothing here has a build-time value baked in. A server address, gate code
 * or RF power that differed between two warehouses would otherwise mean two
 * builds of the same app; instead the installer sets them once on the device
 * and the APK is identical everywhere.
 *
 * <p>The one value with a derived default is the device identity, which falls
 * back to the reader's own serial so a fresh install still reports something
 * meaningful to the server.
 */
public final class AppSettings {

    private static final String FILE = "denim-rolls-settings";

    private static final String KEY_SERVER = "server_url";
    private static final String KEY_GATE = "gate_code";
    private static final String KEY_DEVICE = "device_id";
    private static final String KEY_READ_INTERVAL = "read_interval_ms";
    private static final String KEY_POWER = "rf_power_dbm";
    private static final String KEY_ANTENNA = "antenna_port";
    private static final String KEY_ALARM_OUTPUT = "alarm_output";
    private static final String KEY_SOUND = "sound_enabled";

    /** One accepted EPC per second, as the gate procedure requires. */
    public static final int DEFAULT_READ_INTERVAL_MS = 1000;

    public static final int DEFAULT_POWER_DBM = 25;
    public static final int DEFAULT_ANTENNA = 1;

    /** GPO line driven on an alarm. 0 disables the output entirely. */
    public static final int DEFAULT_ALARM_OUTPUT = 1;

    private final SharedPreferences prefs;

    public AppSettings(Context context) {
        this.prefs = context.getApplicationContext().getSharedPreferences(FILE, Context.MODE_PRIVATE);
    }

    /** Base URL of the warehouse API, e.g. http://192.168.18.51:5080 */
    public String serverUrl() {
        return prefs.getString(KEY_SERVER, "");
    }

    public void setServerUrl(String value) {
        String trimmed = value == null ? "" : value.trim();

        while (trimmed.endsWith("/")) {
            trimmed = trimmed.substring(0, trimmed.length() - 1);
        }

        prefs.edit().putString(KEY_SERVER, trimmed).apply();
    }

    /** Gate this reader is installed on, as configured in the warehouse portal. */
    public String gateCode() {
        return prefs.getString(KEY_GATE, "");
    }

    public void setGateCode(String value) {
        prefs.edit().putString(KEY_GATE, value == null ? "" : value.trim()).apply();
    }

    /** Identity this reader reports. Defaults to the device serial. */
    public String deviceId() {
        String stored = prefs.getString(KEY_DEVICE, "");
        return TextUtils.isEmpty(stored) ? defaultDeviceId() : stored;
    }

    public void setDeviceId(String value) {
        prefs.edit().putString(KEY_DEVICE, value == null ? "" : value.trim()).apply();
    }

    private static String defaultDeviceId() {
        String serial = Build.SERIAL;

        if (TextUtils.isEmpty(serial) || "unknown".equalsIgnoreCase(serial)) {
            serial = Build.MODEL;
        }

        return "U300-" + (TextUtils.isEmpty(serial) ? "UNKNOWN" : serial.replaceAll("\\s+", ""));
    }

    /**
     * Minimum gap between two accepted EPCs. The gate procedure admits one roll
     * per second; the reader itself will report far faster than that, so the
     * surplus is discarded rather than the reader being slowed down.
     */
    public int readIntervalMs() {
        return prefs.getInt(KEY_READ_INTERVAL, DEFAULT_READ_INTERVAL_MS);
    }

    public void setReadIntervalMs(int value) {
        prefs.edit().putInt(KEY_READ_INTERVAL, Math.max(0, value)).apply();
    }

    /** Transmit power in dBm. The U300 accepts 1-30. */
    public int powerDbm() {
        return prefs.getInt(KEY_POWER, DEFAULT_POWER_DBM);
    }

    public void setPowerDbm(int value) {
        prefs.edit().putInt(KEY_POWER, Math.max(1, Math.min(30, value))).apply();
    }

    public int antennaPort() {
        return prefs.getInt(KEY_ANTENNA, DEFAULT_ANTENNA);
    }

    public void setAntennaPort(int value) {
        prefs.edit().putInt(KEY_ANTENNA, Math.max(1, Math.min(8, value))).apply();
    }

    /** GPO line pulsed on an alarm, 1-4. Zero disables it. */
    public int alarmOutput() {
        return prefs.getInt(KEY_ALARM_OUTPUT, DEFAULT_ALARM_OUTPUT);
    }

    public void setAlarmOutput(int value) {
        prefs.edit().putInt(KEY_ALARM_OUTPUT, Math.max(0, Math.min(4, value))).apply();
    }

    public boolean soundEnabled() {
        return prefs.getBoolean(KEY_SOUND, true);
    }

    public void setSoundEnabled(boolean value) {
        prefs.edit().putBoolean(KEY_SOUND, value).apply();
    }

    /** True once the app has enough configuration to reach a server. */
    public boolean isConfigured() {
        return !TextUtils.isEmpty(serverUrl()) && !TextUtils.isEmpty(gateCode());
    }
}
