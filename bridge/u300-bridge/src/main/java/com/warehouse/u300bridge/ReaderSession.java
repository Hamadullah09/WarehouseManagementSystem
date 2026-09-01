package com.warehouse.u300bridge;

import com.rscja.deviceapi.ConnectionState;
import com.rscja.deviceapi.RFIDWithUHFNetworkA4;
import com.rscja.deviceapi.RFIDWithUHFNetworkA8;
import com.rscja.deviceapi.RFIDWithUHFSerialPortA4;
import com.rscja.deviceapi.RFIDWithUHFSerialPortA8;
import com.rscja.deviceapi.entity.AntennaNameEnum;
import com.rscja.deviceapi.entity.AntennaState;
import com.rscja.deviceapi.entity.GPIStateEntity;
import com.rscja.deviceapi.entity.GPOEntity;
import com.rscja.deviceapi.entity.UHFTAGInfo;
import com.rscja.deviceapi.interfaces.ConnectionStateCallback;
import com.rscja.deviceapi.interfaces.IGPIStateCallback;
import com.rscja.deviceapi.interfaces.IUHFA4;
import com.rscja.deviceapi.interfaces.IUHFA8;
import com.rscja.deviceapi.interfaces.IUHFAx;
import com.rscja.deviceapi.interfaces.IUHFInventoryCallback;

import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.logging.Level;
import java.util.logging.Logger;

/**
 * Owns the vendor SDK object and translates its callbacks into bridge events.
 *
 * <p>This is the only class in the system that touches Chainway types. Every
 * operation maps one-to-one onto a documented SDK call and nothing is invented:
 *
 * <pre>
 *   connect          -&gt; RFIDWithUHFNetworkA4/A8.init(ip, port)
 *                       RFIDWithUHFSerialPortA4/A8.init(comPort)
 *   disconnect       -&gt; IUHFAx.free()
 *   startInventory   -&gt; IUHFAx.startInventoryTag()
 *   stopInventory    -&gt; IUHFAx.stopInventory()
 *   setGpo           -&gt; IUHFA4/IUHFA8.outputOnAndOff(List&lt;GPOEntity&gt;)
 *   setAntennaPower  -&gt; IUHFAx.setPower(AntennaNameEnum, dBm)
 *   tag events       &lt;- setInventoryCallback(IUHFInventoryCallback)
 *   GPI events       &lt;- setGPIStateCallback(IGPIStateCallback)
 *   state events     &lt;- setConnectionStateCallback(ConnectionStateCallback)
 * </pre>
 *
 * <p>{@link IUHFAx} is the common supertype of the 4-port and 8-port
 * interfaces and carries everything except the output methods, which are
 * declared separately on {@link IUHFA4} and {@link IUHFA8}.
 *
 * <p><b>GPI is push-only here.</b> The Android AAR exposes a public
 * {@code inputStatus()}, but in the host-side jar (UHFAPI20221125) that method
 * is package-private and returns {@code int[]}. The vendor's own desktop demo
 * therefore reads inputs solely through {@link IGPIStateCallback}, and so does
 * this bridge: the latest level per pin is cached as callbacks arrive and the
 * cache answers {@code readGpi}. A pin that has never fired is reported as
 * absent rather than guessed at.
 */
final class ReaderSession {

    private static final Logger LOG = Logger.getLogger(ReaderSession.class.getName());

    private final BridgeConfig config;
    private final EventSink sink;

    /** Latest known level per GPI pin. Absent means never reported. */
    private final Map<String, Integer> gpiCache = new ConcurrentHashMap<>();

    private final AtomicBoolean inventorying = new AtomicBoolean(false);

    private volatile Object reader;
    private volatile IUHFAx api;
    private volatile boolean connected;

    ReaderSession(BridgeConfig config, EventSink sink) {
        this.config = config;
        this.sink = sink;
    }

    interface EventSink {
        void onTag(Map<String, Object> event);

        void onGpi(Map<String, Object> event);

        void onState(String state, String reason);

        void onError(String operation, String message, String code);
    }

    boolean isConnected() {
        return connected;
    }

    boolean isInventorying() {
        return inventorying.get();
    }

    // ------------------------------------------------------------- lifecycle

    synchronized boolean connect() {
        if (connected) {
            return true;
        }

        try {
            boolean serial = config.serialPort != null && !config.serialPort.isEmpty();
            boolean ok;

            // The Ax base classes that declare init() are package-private in
            // the vendor jar, so each concrete type is constructed and opened
            // in its own branch rather than through a shared supertype.
            if (serial) {
                if (config.eightPort) {
                    RFIDWithUHFSerialPortA8 r = new RFIDWithUHFSerialPortA8();
                    this.reader = r;
                    this.api = r;
                    attachCallbacks();
                    ok = r.init(config.serialPort);
                } else {
                    RFIDWithUHFSerialPortA4 r = new RFIDWithUHFSerialPortA4();
                    this.reader = r;
                    this.api = r;
                    attachCallbacks();
                    ok = r.init(config.serialPort);
                }

                LOG.info("init(" + config.serialPort + ") -> " + ok);
            } else {
                // The vendor's init() blocks on a raw TCP connect, which on an
                // unreachable reader means waiting out the OS timeout with no
                // answer to the caller. Probe first so an unplugged or
                // mis-addressed reader fails in a second with a clear reason.
                if (!isReachable()) {
                    this.reader = null;
                    this.api = null;

                    String target = config.readerHost + ":" + config.readerPort;
                    LOG.warning("Reader " + target + " is not reachable");
                    sink.onError("init", "Reader " + target + " is not reachable.", "UNREACHABLE");

                    return false;
                }

                if (config.eightPort) {
                    RFIDWithUHFNetworkA8 r = new RFIDWithUHFNetworkA8();
                    this.reader = r;
                    this.api = r;
                    attachCallbacks();
                    ok = r.init(config.readerHost, config.readerPort);
                } else {
                    RFIDWithUHFNetworkA4 r = new RFIDWithUHFNetworkA4();
                    this.reader = r;
                    this.api = r;
                    attachCallbacks();
                    ok = r.init(config.readerHost, config.readerPort);
                }

                LOG.info("init(" + config.readerHost + ", " + config.readerPort + ") -> " + ok);
            }

            if (!ok) {
                this.reader = null;
                this.api = null;
                sink.onError("init", "The reader refused the connection.", "INIT_FAILED");

                return false;
            }

            connected = true;
            applyConfiguration();
            sink.onState("CONNECTED", null);

            return true;
        } catch (Throwable t) {
            // Throwable, not Exception: a missing native library surfaces as
            // UnsatisfiedLinkError and must not take the bridge down.
            this.reader = null;
            this.api = null;
            connected = false;

            LOG.log(Level.SEVERE, "connect failed", t);
            sink.onError("init", describe(t), "EXCEPTION");

            return false;
        }
    }

    synchronized boolean disconnect() {
        IUHFAx a = api;

        if (a == null) {
            connected = false;
            return true;
        }

        try {
            if (inventorying.get()) {
                stopInventory();
            }

            boolean ok = a.free();
            LOG.info("free() -> " + ok);

            return ok;
        } catch (Throwable t) {
            LOG.log(Level.WARNING, "free failed", t);
            sink.onError("free", describe(t), "EXCEPTION");

            return false;
        } finally {
            reader = null;
            api = null;
            connected = false;
            inventorying.set(false);
            sink.onState("DISCONNECTED", "Closed by request");
        }
    }

    /** Applies antenna selection and per-antenna power from configuration. */
    private void applyConfiguration() {
        IUHFAx a = api;

        if (a == null) {
            return;
        }

        try {
            if (!config.antennas.isEmpty()) {
                // setAntenna takes the full port state, not a list of ports to
                // enable, so every port the reader has is named explicitly and
                // the ones not configured are switched off. Leaving a stray
                // port enabled is how a gate ends up reading the next aisle.
                int ports = config.eightPort ? 8 : 4;
                List<AntennaState> states = new ArrayList<>();

                for (int port = 1; port <= ports; port++) {
                    AntennaNameEnum ant = antennaFor(port);

                    if (ant != null) {
                        states.add(new AntennaState(ant, config.antennas.contains(port)));
                    }
                }

                if (!states.isEmpty()) {
                    boolean ok = a.setAntenna(states);
                    LOG.info("setAntenna(enabled=" + config.antennas + " of " + ports + ") -> " + ok);
                }
            }

            for (Map.Entry<Integer, Integer> entry : config.antennaPower.entrySet()) {
                setPower(entry.getKey(), entry.getValue());
            }

            // Without this the reader repeats the same tag continuously while
            // it sits in the field. The application deduplicates regardless,
            // but throttling at source keeps the link quiet.
            if (config.filterRepeatMs > 0 && reader instanceof RFIDWithUHFNetworkA4) {
                ((RFIDWithUHFNetworkA4) reader).setFilterRepeatData(config.filterRepeatMs);
            }
        } catch (Throwable t) {
            LOG.log(Level.WARNING, "applyConfiguration failed", t);
            sink.onError("applyConfiguration", describe(t), "EXCEPTION");
        }
    }

    // ------------------------------------------------------------ operations

    synchronized boolean startInventory() {
        IUHFAx a = api;

        if (a == null) {
            return false;
        }

        try {
            boolean ok = a.startInventoryTag();
            inventorying.set(ok);

            if (!ok) {
                sink.onError("startInventoryTag", "The reader rejected the start command.", "START_FAILED");
            }

            return ok;
        } catch (Throwable t) {
            LOG.log(Level.WARNING, "startInventoryTag failed", t);
            sink.onError("startInventoryTag", describe(t), "EXCEPTION");

            return false;
        }
    }

    synchronized boolean stopInventory() {
        IUHFAx a = api;

        if (a == null) {
            return false;
        }

        try {
            boolean ok = a.stopInventory();
            inventorying.set(false);

            if (!ok) {
                sink.onError("stopInventory", "The reader rejected the stop command.", "STOP_FAILED");
            }

            return ok;
        } catch (Throwable t) {
            inventorying.set(false);

            LOG.log(Level.WARNING, "stopInventory failed", t);
            sink.onError("stopInventory", describe(t), "EXCEPTION");

            return false;
        }
    }

    synchronized boolean setOutputs(List<Map<String, Object>> outputs) {
        Object r = reader;

        if (r == null) {
            return false;
        }

        try {
            List<GPOEntity> list = new ArrayList<>();

            for (Map<String, Object> output : outputs) {
                String pin = Json.str(output, "pin");

                if (pin == null || pin.isEmpty()) {
                    continue;
                }

                list.add(new GPOEntity(pin, Json.boolOf(output, "high", false) ? 1 : 0));
            }

            if (list.isEmpty()) {
                return true;
            }

            // outputOnAndOff is declared on IUHFA4 and IUHFA8 separately, not
            // on their shared supertype.
            if (r instanceof IUHFA4) {
                ((IUHFA4) r).outputOnAndOff(list);
            } else if (r instanceof IUHFA8) {
                ((IUHFA8) r).outputOnAndOff(list);
            } else {
                sink.onError("outputOnAndOff", "Reader does not expose GPIO outputs.", "UNSUPPORTED");
                return false;
            }

            // The vendor method returns void; success is the absence of a throw.
            return true;
        } catch (Throwable t) {
            LOG.log(Level.WARNING, "outputOnAndOff failed", t);
            sink.onError("outputOnAndOff", describe(t), "EXCEPTION");

            return false;
        }
    }

    synchronized boolean setPower(int antenna, int dbm) {
        IUHFAx a = api;

        if (a == null) {
            return false;
        }

        AntennaNameEnum ant = antennaFor(antenna);

        if (ant == null) {
            sink.onError("setPower", "Unsupported antenna port " + antenna, "BAD_ANTENNA");
            return false;
        }

        try {
            boolean ok = a.setPower(ant, dbm);
            LOG.info("setPower(ANT" + antenna + ", " + dbm + ") -> " + ok);

            return ok;
        } catch (Throwable t) {
            LOG.log(Level.WARNING, "setPower failed", t);
            sink.onError("setPower", describe(t), "EXCEPTION");

            return false;
        }
    }

    /** Latest cached GPI levels. Pins never reported are omitted. */
    List<Map<String, Object>> inputSnapshot() {
        List<Map<String, Object>> list = new ArrayList<>();

        for (Map.Entry<String, Integer> entry : gpiCache.entrySet()) {
            Map<String, Object> item = Json.obj();
            item.put("pin", entry.getKey());
            item.put("state", entry.getValue());
            list.add(item);
        }

        return list;
    }

    /** Reader identity and health for the status command. */
    Map<String, Object> status() {
        Map<String, Object> map = Json.obj();
        map.put("inventorying", inventorying.get());
        map.put("inputs", inputSnapshot());

        IUHFAx a = api;

        if (a == null) {
            return map;
        }

        // Each probe is optional: diagnostics must never fail a status call.
        try {
            map.put("firmware", a.getVersion());
        } catch (Throwable ignored) {
            LOG.fine("getVersion unavailable");
        }

        try {
            map.put("hardware", a.getAndroidDeviceHardwareVersion());
        } catch (Throwable ignored) {
            LOG.fine("getAndroidDeviceHardwareVersion unavailable");
        }

        try {
            map.put("temperature", (double) a.getTemperature());
        } catch (Throwable ignored) {
            LOG.fine("getTemperature unavailable");
        }

        try {
            List<Integer> ports = new ArrayList<>();

            for (Object item : a.getAntenna()) {
                if (item instanceof AntennaState) {
                    AntennaState state = (AntennaState) item;

                    if (state.isEnable() && state.getAntennaName() != null) {
                        ports.add(state.getAntennaName().getValue());
                    }
                }
            }

            map.put("antennas", ports);
        } catch (Throwable ignored) {
            LOG.fine("getAntenna unavailable");
        }

        return map;
    }

    // ------------------------------------------------------------- callbacks

    private void attachCallbacks() {
        final IUHFAx a = api;

        if (a == null) {
            return;
        }

        a.setConnectionStateCallback(new ConnectionStateCallback() {
            @Override
            public void getState(ConnectionState state, Object data) {
                boolean up = state == ConnectionState.CONNECTED;
                connected = up;

                if (!up) {
                    inventorying.set(false);
                }

                LOG.info("connection state -> " + state);
                sink.onState(up ? "CONNECTED" : "DISCONNECTED", String.valueOf(state));
            }
        });

        a.setInventoryCallback(new IUHFInventoryCallback() {
            @Override
            public void callback(UHFTAGInfo info) {
                if (info == null) {
                    return;
                }

                try {
                    Map<String, Object> event = Json.obj();
                    event.put("type", "tag");
                    event.put("epc", info.getEPC());
                    event.put("tid", info.getTid());
                    event.put("user", info.getUser());
                    event.put("pc", info.getPc());
                    event.put("rssi", info.getRssi());
                    event.put("ant", info.getAnt());
                    event.put("count", info.getCount());
                    event.put("ts", System.currentTimeMillis());

                    sink.onTag(event);
                } catch (Throwable t) {
                    LOG.log(Level.WARNING, "tag callback failed", t);
                }
            }
        });

        a.setGPIStateCallback(new IGPIStateCallback() {
            @Override
            @SuppressWarnings("rawtypes")
            public void callback(List list) {
                if (list == null) {
                    return;
                }

                try {
                    for (Object item : list) {
                        if (!(item instanceof GPIStateEntity)) {
                            continue;
                        }

                        GPIStateEntity entity = (GPIStateEntity) item;
                        String pin = entity.getGPIName();

                        if (pin == null) {
                            continue;
                        }

                        int state = entity.getGPIState();
                        Integer previous = gpiCache.put(pin, state);

                        // Report genuine edges only. The reader can repeat the
                        // current level, and a repeated "high" must never look
                        // like a second gate event.
                        if (previous != null && previous == state) {
                            continue;
                        }

                        Map<String, Object> event = Json.obj();
                        event.put("type", "gpi");
                        event.put("pin", pin);
                        event.put("state", String.valueOf(state));
                        event.put("ts", System.currentTimeMillis());

                        sink.onGpi(event);
                    }
                } catch (Throwable t) {
                    LOG.log(Level.WARNING, "GPI callback failed", t);
                }
            }
        });

        if (config.reverseGpi) {
            a.setGPIStateReverse(true);
        }
    }

    // --------------------------------------------------------------- helpers

    /** Short TCP probe of the reader's service port. */
    private boolean isReachable() {
        try (Socket probe = new Socket()) {
            probe.connect(new InetSocketAddress(config.readerHost, config.readerPort), config.connectProbeMs);
            return true;
        } catch (Exception ex) {
            return false;
        }
    }

    private static AntennaNameEnum antennaFor(int port) {
        try {
            return AntennaNameEnum.valueOf("ANT" + port);
        } catch (IllegalArgumentException ex) {
            return null;
        }
    }

    private static String describe(Throwable t) {
        String message = t.getMessage();
        return t.getClass().getSimpleName() + (message == null ? "" : ": " + message);
    }

    /** Read-only view used by the server for logging. */
    Map<String, Object> describeConfig() {
        Map<String, Object> map = new LinkedHashMap<>();
        map.put("readerId", config.readerId);
        map.put("transport", config.serialPort == null ? "network" : "serial");
        map.put("target", config.serialPort == null
                ? config.readerHost + ":" + config.readerPort
                : config.serialPort);

        return map;
    }
}
