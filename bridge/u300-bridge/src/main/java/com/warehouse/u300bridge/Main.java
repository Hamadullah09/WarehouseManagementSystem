package com.warehouse.u300bridge;

import java.util.logging.ConsoleHandler;
import java.util.logging.Level;
import java.util.logging.Logger;
import java.util.logging.SimpleFormatter;

/**
 * Entry point for the U300 bridge.
 *
 * <p>Usage: {@code java -jar u300-bridge.jar [config.properties]}
 *
 * <p>One process serves one reader. Run several instances on different listen
 * ports to serve several gates.
 */
public final class Main {

    private Main() {
    }

    public static void main(String[] args) {
        configureLogging();

        Logger log = Logger.getLogger(Main.class.getName());

        try {
            BridgeConfig config = BridgeConfig.load(args);

            log.info("U300 bridge starting: " + config);

            new BridgeServer(config).run();
        } catch (Throwable t) {
            log.log(Level.SEVERE, "Bridge failed to start", t);
            System.exit(1);
        }
    }

    private static void configureLogging() {
        Logger root = Logger.getLogger("");

        for (java.util.logging.Handler handler : root.getHandlers()) {
            root.removeHandler(handler);
        }

        ConsoleHandler handler = new ConsoleHandler();
        handler.setFormatter(new SimpleFormatter());
        handler.setLevel(Level.ALL);

        root.addHandler(handler);

        String level = System.getenv("BRIDGE_LOG_LEVEL");
        root.setLevel(level == null ? Level.INFO : Level.parse(level));
    }
}
