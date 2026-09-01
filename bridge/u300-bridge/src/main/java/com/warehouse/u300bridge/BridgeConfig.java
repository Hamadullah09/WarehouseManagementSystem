package com.warehouse.u300bridge;

import java.io.IOException;
import java.io.InputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Properties;

/**
 * Bridge configuration, read from a properties file and overridable by
 * environment variables. Nothing here is hard-coded in source (§34, §44).
 *
 * <p>Environment overrides use the property name uppercased with dots replaced
 * by underscores, e.g. {@code reader.host} becomes {@code READER_HOST}. That
 * makes the bridge deployable as a container or a Windows service without
 * editing files on the host.
 */
final class BridgeConfig {

    /** Identifier echoed to the .NET side; must match the Readers row. */
    String readerId = "U300-01";

    /** Address the bridge listens on for the .NET adapter. */
    String listenHost = "127.0.0.1";

    int listenPort = 9310;

    /** Reader address. Vendor default per the U300 manual. */
    String readerHost = "192.168.1.100";

    /** Vendor RAW-protocol service port. Default 9160 per the manual. */
    int readerPort = 9160;

    /** Set to use RS-232 instead of Ethernet, e.g. COM3 or /dev/ttyUSB0. */
    String serialPort;

    /** True for U300-8 / URA8 hardware, which uses the 8-port SDK classes. */
    boolean eightPort;

    /** Antenna ports to enable, 1-based. Empty leaves the reader as configured. */
    List<Integer> antennas = new ArrayList<>();

    /** Transmit power per antenna port, in dBm. U300 supports 1-30. */
    Map<Integer, Integer> antennaPower = new LinkedHashMap<>();

    /** Suppress repeated reports of the same tag for this many ms. Zero disables. */
    int filterRepeatMs;

    /** Invert the logical sense of the GPI lines at the reader. */
    boolean reverseGpi;

    /** How often to emit a heartbeat to connected clients, milliseconds. */
    int heartbeatMs = 5_000;

    /** Delay between reader reconnect attempts, milliseconds. */
    int reconnectMs = 5_000;

    /**
     * How long to probe the reader's TCP port before giving up, milliseconds.
     * Keeps an unreachable reader from blocking on the OS connect timeout.
     */
    int connectProbeMs = 3_000;

    static BridgeConfig load(String[] args) throws IOException {
        BridgeConfig config = new BridgeConfig();
        Properties props = new Properties();

        Path file = args.length > 0
                ? Paths.get(args[0])
                : Paths.get("bridge.properties");

        if (Files.exists(file)) {
            try (InputStream in = Files.newInputStream(file)) {
                props.load(in);
            }

            System.out.println("[config] loaded " + file.toAbsolutePath());
        } else {
            System.out.println("[config] " + file.toAbsolutePath() + " not found; using defaults and environment");
        }

        config.readerId = get(props, "reader.id", config.readerId);
        config.listenHost = get(props, "listen.host", config.listenHost);
        config.listenPort = getInt(props, "listen.port", config.listenPort);
        config.readerHost = get(props, "reader.host", config.readerHost);
        config.readerPort = getInt(props, "reader.port", config.readerPort);
        config.serialPort = get(props, "reader.serial", null);
        config.eightPort = getBool(props, "reader.eightPort", false);
        config.filterRepeatMs = getInt(props, "reader.filterRepeatMs", 0);
        config.reverseGpi = getBool(props, "reader.reverseGpi", false);
        config.heartbeatMs = getInt(props, "bridge.heartbeatMs", config.heartbeatMs);
        config.reconnectMs = getInt(props, "bridge.reconnectMs", config.reconnectMs);
        config.connectProbeMs = getInt(props, "bridge.connectProbeMs", config.connectProbeMs);

        String antennas = get(props, "reader.antennas", "1");

        for (String part : antennas.split(",")) {
            String trimmed = part.trim();

            if (!trimmed.isEmpty()) {
                config.antennas.add(Integer.parseInt(trimmed));
            }
        }

        // reader.power.1=30, reader.power.2=25, ...
        for (Integer port : config.antennas) {
            String value = get(props, "reader.power." + port, null);

            if (value != null && !value.trim().isEmpty()) {
                config.antennaPower.put(port, Integer.parseInt(value.trim()));
            }
        }

        return config;
    }

    private static String get(Properties props, String key, String fallback) {
        String env = System.getenv(key.toUpperCase().replace('.', '_'));

        if (env != null && !env.isEmpty()) {
            return env;
        }

        String value = props.getProperty(key);

        return value == null || value.trim().isEmpty() ? fallback : value.trim();
    }

    private static int getInt(Properties props, String key, int fallback) {
        String value = get(props, key, null);
        return value == null ? fallback : Integer.parseInt(value);
    }

    private static boolean getBool(Properties props, String key, boolean fallback) {
        String value = get(props, key, null);
        return value == null ? fallback : Boolean.parseBoolean(value);
    }

    @Override
    public String toString() {
        return "readerId=" + readerId
                + ", listen=" + listenHost + ":" + listenPort
                + ", reader=" + (serialPort == null ? readerHost + ":" + readerPort : serialPort)
                + ", eightPort=" + eightPort
                + ", antennas=" + antennas
                + ", power=" + antennaPower;
    }
}
