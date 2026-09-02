package com.smatechnology.denimrolls.rfid;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import com.rscja.deviceapi.RFIDWithUHFA4;
import com.rscja.deviceapi.entity.UHFTAGInfo;
import com.rscja.deviceapi.interfaces.IUHFInventoryCallback;
import com.smatechnology.denimrolls.data.AppSettings;

import java.util.LinkedHashSet;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * The reader, as this app uses it.
 *
 * <p>Wraps {@link RFIDWithUHFA4} from the Chainway DeviceAPI, which drives the
 * U300's own UHF module from an app running on the reader. Every call below
 * maps to a documented SDK method:
 *
 * <pre>
 *   getInstance() / init(context)   open the module
 *   setPower(dBm) / setANT(list)    apply configuration
 *   setInventoryCallback(...)       receive tags
 *   startInventoryTag()             begin reading
 *   stopInventory()                 stop
 *   output1On() .. output4Off()     drive the alarm beacon
 *   buzzer() / led() / successNotify()  local operator feedback
 *   free()                          release the module
 * </pre>
 *
 * <h3>Reading pace</h3>
 *
 * Tags surface as fast as the reader finds them. The reader reports the same
 * tag many times a second, so each distinct tag is released once and the
 * repeats are dropped -- but no artificial delay is imposed between different
 * tags. Holding tags back would only make a fast pass look like a short read.
 *
 * <p>Silence is what gets watched instead: {@code ScanActivity} raises the
 * no-tag alarm when nothing has been read for the configured window, which is
 * what a roll going past without a working tag actually looks like.
 */
public final class ReaderController {

    private static final String TAG = "ReaderController";

    /**
     * Nearly every failure to open the module has the same cause, so the
     * message says what to do about it rather than restating that something
     * went wrong. Only one program can hold the UHF module: if the built-in
     * transmission service (the one serving ports 9160/9260/8080) is running,
     * this app cannot have it, and the reverse is equally true.
     */
    private static final String MODULE_BUSY =
            "The RFID module is being used by another program.\n\n"
            + "Only one program can use it at a time. On the reader, open "
            + "\"UHF service\" and stop it, then press START again.";

    public interface Listener {

        /** The module opened. Version is whatever the SDK reports. */
        void onReaderReady(String version);

        /** Something the operator needs to know about. Never fatal to the app. */
        void onReaderError(String message);

        /** A distinct tag, released at the configured pace. */
        void onEpcAccepted(String epc, String rssi, String antenna);

        /** Reading actually started or stopped. */
        void onInventoryStateChanged(boolean running);
    }

    private final Context context;
    private final AppSettings settings;
    private final Handler main = new Handler(Looper.getMainLooper());

    /** Every tag seen this session, so a tag is queued once however often it repeats. */
    private final Set<String> seen = new LinkedHashSet<>();

    private final AtomicBoolean running = new AtomicBoolean(false);
    private final AtomicInteger rawReads = new AtomicInteger(0);
    private final AtomicBoolean healthy = new AtomicBoolean(true);

    private RFIDWithUHFA4 reader;
    private Listener listener;

    public ReaderController(Context context) {
        this.context = context.getApplicationContext();
        this.settings = new AppSettings(context);
    }

    public void setListener(Listener listener) {
        this.listener = listener;
    }

    public boolean isRunning() {
        return running.get();
    }

    public int rawReadCount() {
        return rawReads.get();
    }

    public boolean isHealthy() {
        return healthy.get();
    }

    // ------------------------------------------------------------- lifecycle

    /**
     * Opens the UHF module and applies configuration. Safe to call twice.
     * Returns false and reports the reason rather than throwing, because a
     * reader fault must not take the app down in front of an operator.
     */
    public boolean initialise() {
        try {
            if (reader == null) {
                reader = RFIDWithUHFA4.getInstance();
            }

            if (!reader.init(context)) {
                healthy.set(false);
                report(MODULE_BUSY);

                return false;
            }

            reader.setInventoryCallback(inventoryCallback);

            applyConfiguration();

            healthy.set(true);

            String version = safeVersion();

            if (listener != null) {
                main.post(() -> listener.onReaderReady(version));
            }

            return true;
        } catch (Throwable t) {
            // Throwable: a missing native library arrives as UnsatisfiedLinkError.
            Log.e(TAG, "init failed", t);
            healthy.set(false);
            report(MODULE_BUSY + "\n\n(" + describe(t) + ")");

            return false;
        }
    }

    private void applyConfiguration() {
        try {
            reader.setPower(settings.powerDbm());
        } catch (Throwable t) {
            Log.w(TAG, "setPower failed", t);
            report("Transmit power could not be set; the reader is using its stored value.");
        }

        try {
            // EPC-only inventory: the gate matches on EPC, and asking for TID
            // or user memory as well costs read rate for data nothing uses.
            reader.setEPCMode();
        } catch (Throwable t) {
            Log.w(TAG, "setEPCMode failed", t);
        }
    }

    private String safeVersion() {
        try {
            String version = reader.getVersion();
            return version == null ? "" : version;
        } catch (Throwable t) {
            return "";
        }
    }

    /** Starts a read session. Clears any state from the previous one. */
    public boolean start() {
        if (reader == null && !initialise()) {
            return false;
        }

        if (running.get()) {
            return true;
        }

        synchronized (seen) {
            seen.clear();
        }

        rawReads.set(0);
        healthy.set(true);

        try {
            if (!reader.startInventoryTag()) {
                healthy.set(false);
                report(MODULE_BUSY);

                return false;
            }
        } catch (Throwable t) {
            Log.e(TAG, "startInventoryTag failed", t);
            healthy.set(false);
            report(MODULE_BUSY + "\n\n(" + describe(t) + ")");

            return false;
        }

        running.set(true);

        if (listener != null) {
            main.post(() -> listener.onInventoryStateChanged(true));
        }

        return true;
    }

    /** Stops the session. Any tags still queued are released first. */
    public void stop() {
        if (!running.getAndSet(false)) {
            return;
        }

        try {
            if (reader != null) {
                reader.stopInventory();
            }
        } catch (Throwable t) {
            Log.w(TAG, "stopInventory failed", t);
            healthy.set(false);
        }

        if (listener != null) {
            main.post(() -> listener.onInventoryStateChanged(false));
        }
    }

    /** Releases the module. Call from the owning screen's onDestroy. */
    public void release() {
        stop();

        try {
            if (reader != null) {
                reader.free();
            }
        } catch (Throwable t) {
            Log.w(TAG, "free failed", t);
        }

        reader = null;
        listener = null;
    }

    // ------------------------------------------------------------ tag intake

    private final IUHFInventoryCallback inventoryCallback = new IUHFInventoryCallback() {
        @Override
        public void callback(UHFTAGInfo info) {
            if (info == null || info.getEPC() == null || info.getEPC().isEmpty()) {
                return;
            }

            rawReads.incrementAndGet();

            String epc = info.getEPC().trim().toUpperCase();

            // Release each distinct tag once, however many times it repeats.
            synchronized (seen) {
                if (!seen.add(epc)) {
                    return;
                }
            }

            release(info);
        }
    };

    private void release(UHFTAGInfo info) {
        if (listener == null) {
            return;
        }

        final String epc = info.getEPC().trim().toUpperCase();
        final String rssi = info.getRssi() == null ? "" : info.getRssi();
        final String ant = info.getAnt() == null ? "" : info.getAnt();

        main.post(() -> {
            if (listener != null) {
                listener.onEpcAccepted(epc, rssi, ant);
            }
        });
    }

    // ---------------------------------------------------------------- output

    /** Short confirmation for a roll that belongs to the document. */
    public void signalAccepted() {
        if (!settings.soundEnabled()) {
            return;
        }

        try {
            if (reader != null) {
                reader.successNotify();
            }
        } catch (Throwable t) {
            Log.w(TAG, "successNotify failed", t);
        }
    }

    /**
     * Alarm: sounder plus the configured optocoupler output, which is what a
     * beacon or a barrier relay hangs off. The output is released on a timer
     * so a stuck alarm cannot latch the beacon on for ever.
     */
    public void signalAlarm() {
        try {
            if (reader != null && settings.soundEnabled()) {
                reader.buzzer();
                reader.led();
            }
        } catch (Throwable t) {
            Log.w(TAG, "buzzer failed", t);
        }

        final int line = settings.alarmOutput();

        if (line < 1 || reader == null) {
            return;
        }

        try {
            setOutput(line, true);
            main.postDelayed(() -> {
                try {
                    setOutput(line, false);
                } catch (Throwable t) {
                    Log.w(TAG, "could not release alarm output", t);
                }
            }, 1500);
        } catch (Throwable t) {
            Log.w(TAG, "could not drive alarm output", t);
        }
    }

    private void setOutput(int line, boolean on) {
        if (reader == null) {
            return;
        }

        switch (line) {
            case 1: if (on) reader.output1On(); else reader.output1Off(); break;
            case 2: if (on) reader.output2On(); else reader.output2Off(); break;
            case 3: if (on) reader.output3On(); else reader.output3Off(); break;
            case 4: if (on) reader.output4On(); else reader.output4Off(); break;
            default: break;
        }
    }

    // --------------------------------------------------------------- helpers

    private void report(String message) {
        if (listener != null) {
            main.post(() -> {
                if (listener != null) {
                    listener.onReaderError(message);
                }
            });
        }
    }

    private static String describe(Throwable t) {
        String message = t.getMessage();
        return t.getClass().getSimpleName() + (message == null ? "" : ": " + message);
    }
}
