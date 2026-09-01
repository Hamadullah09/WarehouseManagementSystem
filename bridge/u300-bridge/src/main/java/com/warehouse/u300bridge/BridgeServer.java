package com.warehouse.u300bridge;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.logging.Level;
import java.util.logging.Logger;

/**
 * TCP server exposing one U300 reader to the warehouse backend.
 *
 * <p>Framing is one JSON object per line, UTF-8. Commands carry an {@code id}
 * and receive exactly one {@code ack}; tag reads, GPI edges, connection-state
 * changes, errors and heartbeats arrive unsolicited.
 *
 * <p>The reader is opened once and shared by every connected client, so a
 * dashboard can observe the same stream the gate service consumes without
 * disturbing it. Writes are serialised per client and a client that stops
 * reading is dropped rather than allowed to block the SDK callback thread.
 */
final class BridgeServer implements ReaderSession.EventSink {

    private static final Logger LOG = Logger.getLogger(BridgeServer.class.getName());

    private final BridgeConfig config;
    private final ReaderSession session;
    private final List<ClientConnection> clients = new CopyOnWriteArrayList<>();
    private final ScheduledExecutorService scheduler = Executors.newScheduledThreadPool(2, runnable -> {
        Thread thread = new Thread(runnable, "bridge-scheduler");
        thread.setDaemon(true);
        return thread;
    });

    private volatile boolean running = true;

    BridgeServer(BridgeConfig config) {
        this.config = config;
        this.session = new ReaderSession(config, this);
    }

    void run() throws IOException {
        try (ServerSocket server = new ServerSocket()) {
            server.setReuseAddress(true);
            server.bind(new InetSocketAddress(InetAddress.getByName(config.listenHost), config.listenPort));

            LOG.info("U300 bridge listening on " + config.listenHost + ":" + config.listenPort);
            LOG.info("Reader configuration: " + config);

            scheduler.scheduleAtFixedRate(
                    this::heartbeat, config.heartbeatMs, config.heartbeatMs, TimeUnit.MILLISECONDS);

            scheduler.scheduleAtFixedRate(
                    this::ensureReaderConnected, config.reconnectMs, config.reconnectMs, TimeUnit.MILLISECONDS);

            Runtime.getRuntime().addShutdownHook(new Thread(this::shutdown, "bridge-shutdown"));

            while (running) {
                Socket socket = server.accept();
                socket.setTcpNoDelay(true);

                ClientConnection client = new ClientConnection(socket);
                clients.add(client);

                Thread thread = new Thread(client, "bridge-client-" + socket.getPort());
                thread.setDaemon(true);
                thread.start();
            }
        }
    }

    private void shutdown() {
        running = false;

        LOG.info("Shutting down; releasing reader");

        try {
            session.disconnect();
        } catch (Throwable t) {
            LOG.log(Level.WARNING, "Error releasing reader on shutdown", t);
        }

        scheduler.shutdownNow();
    }

    /**
     * Reopens the reader if it has dropped, but only while a client is
     * attached. A bridge nobody is listening to has no reason to hold the
     * reader open.
     */
    private void ensureReaderConnected() {
        try {
            if (!clients.isEmpty() && !session.isConnected()) {
                LOG.info("Reader not connected; attempting to open it");
                session.connect();
            }
        } catch (Throwable t) {
            LOG.log(Level.WARNING, "Reconnect attempt failed", t);
        }
    }

    private void heartbeat() {
        try {
            Map<String, Object> event = Json.obj();
            event.put("type", "heartbeat");
            event.put("connection", session.isConnected() ? "CONNECTED" : "DISCONNECTED");
            event.put("inventorying", session.isInventorying());
            event.put("ts", System.currentTimeMillis());

            broadcast(event);
        } catch (Throwable t) {
            LOG.log(Level.WARNING, "Heartbeat failed", t);
        }
    }

    // -------------------------------------------------- ReaderSession.EventSink

    @Override
    public void onTag(Map<String, Object> event) {
        broadcast(event);
    }

    @Override
    public void onGpi(Map<String, Object> event) {
        broadcast(event);
    }

    @Override
    public void onState(String state, String reason) {
        Map<String, Object> event = Json.obj();
        event.put("type", "state");
        event.put("connection", state);
        event.put("reason", reason);
        event.put("ts", System.currentTimeMillis());

        broadcast(event);
    }

    @Override
    public void onError(String operation, String message, String code) {
        Map<String, Object> event = Json.obj();
        event.put("type", "error");
        event.put("op", operation);
        event.put("message", message);
        event.put("code", code);
        event.put("ts", System.currentTimeMillis());

        broadcast(event);
    }

    private void broadcast(Map<String, Object> event) {
        String line = Json.write(event);

        for (ClientConnection client : clients) {
            client.send(line);
        }
    }

    // ------------------------------------------------------------- commands

    private Map<String, Object> handle(Map<String, Object> request) {
        String id = Json.str(request, "id");
        String command = Json.str(request, "cmd");

        Map<String, Object> ack = Json.obj();
        ack.put("type", "ack");
        ack.put("id", id);

        if (command == null) {
            ack.put("ok", false);
            ack.put("error", "Missing 'cmd'.");

            return ack;
        }

        try {
            switch (command) {
                case "connect":
                    ack.put("ok", session.connect());
                    break;

                case "disconnect":
                    ack.put("ok", session.disconnect());
                    break;

                case "startInventory":
                    ack.put("ok", session.startInventory());
                    break;

                case "stopInventory":
                    ack.put("ok", session.stopInventory());
                    break;

                case "readGpi":
                    ack.put("ok", true);
                    ack.put("inputs", session.inputSnapshot());
                    break;

                case "setGpo":
                    ack.put("ok", session.setOutputs(outputsOf(request)));
                    break;

                case "setAntennaPower":
                    ack.put("ok", applyPower(request));
                    break;

                case "status":
                    ack.put("ok", true);
                    ack.putAll(session.status());
                    break;

                case "ping":
                    ack.put("ok", true);
                    ack.put("ts", (double) System.currentTimeMillis());
                    break;

                default:
                    ack.put("ok", false);
                    ack.put("error", "Unknown command: " + command);
            }
        } catch (Throwable t) {
            LOG.log(Level.WARNING, "Command " + command + " failed", t);

            ack.put("ok", false);
            ack.put("error", t.getClass().getSimpleName() + ": " + t.getMessage());
            ack.put("code", "EXCEPTION");
        }

        return ack;
    }

    @SuppressWarnings("unchecked")
    private static List<Map<String, Object>> outputsOf(Map<String, Object> request) {
        Object raw = request.get("outputs");
        List<Map<String, Object>> outputs = new ArrayList<>();

        if (raw instanceof List) {
            for (Object item : (List<Object>) raw) {
                if (item instanceof Map) {
                    outputs.add((Map<String, Object>) item);
                }
            }
        }

        return outputs;
    }

    @SuppressWarnings("unchecked")
    private boolean applyPower(Map<String, Object> request) {
        Object raw = request.get("power");

        if (!(raw instanceof Map)) {
            return false;
        }

        boolean all = true;

        for (Map.Entry<String, Object> entry : ((Map<String, Object>) raw).entrySet()) {
            int antenna = Integer.parseInt(entry.getKey().trim());
            int dbm = ((Number) entry.getValue()).intValue();

            all &= session.setPower(antenna, dbm);
        }

        return all;
    }

    // --------------------------------------------------------------- client

    /** One connected backend. Reads commands, writes acks and events. */
    private final class ClientConnection implements Runnable {

        private final Socket socket;
        private final Writer writer;
        private final Object writeLock = new Object();

        ClientConnection(Socket socket) throws IOException {
            this.socket = socket;
            this.writer = new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8);
        }

        void send(String line) {
            synchronized (writeLock) {
                try {
                    writer.write(line);
                    writer.write('\n');
                    writer.flush();
                } catch (IOException ex) {
                    // The peer is gone. Close and let the read loop clean up.
                    close();
                }
            }
        }

        @Override
        public void run() {
            LOG.info("Client connected from " + socket.getRemoteSocketAddress());

            try (BufferedReader reader =
                         new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8))) {

                String line;

                while ((line = reader.readLine()) != null) {
                    if (line.trim().isEmpty()) {
                        continue;
                    }

                    Map<String, Object> request;

                    try {
                        request = Json.parseObject(line);
                    } catch (RuntimeException ex) {
                        LOG.warning("Discarding unparseable command: " + ex.getMessage());
                        continue;
                    }

                    send(Json.write(handle(request)));
                }
            } catch (IOException ex) {
                LOG.fine("Client read loop ended: " + ex.getMessage());
            } finally {
                close();
                clients.remove(this);

                LOG.info("Client disconnected from " + socket.getRemoteSocketAddress());

                // Last client out releases the reader, so a restarted backend
                // never fights a stale session for the device.
                if (clients.isEmpty() && session.isConnected()) {
                    LOG.info("No clients remain; releasing reader");
                    session.disconnect();
                }
            }
        }

        private void close() {
            try {
                socket.close();
            } catch (IOException ignored) {
                // already closed
            }
        }
    }
}
