package com.smatechnology.denimrolls.ui;

import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.TextUtils;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;

import com.google.android.material.materialswitch.MaterialSwitch;
import com.google.android.material.textfield.TextInputEditText;
import com.smatechnology.denimrolls.R;
import com.smatechnology.denimrolls.data.AppSettings;
import com.smatechnology.denimrolls.rfid.ReaderController;

import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Deployment settings.
 *
 * <p>This screen exists so the APK is identical in every warehouse. Server
 * address, gate, read pace, RF power, which optocoupler drives the beacon and
 * which input carries the gate signal are all site facts, and none of them is
 * compiled in.
 *
 * <p>The gate input also gets a live readout. Which terminal a signal was
 * landed on is not something anybody can tell by looking at the code, so the
 * screen shows all four pins changing in real time and the installer reads the
 * answer off the reader itself.
 */
public final class SettingsActivity extends AppCompatActivity {

    private static final long GPI_POLL_MS = 300L;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService io = Executors.newSingleThreadExecutor();

    private AppSettings settings;
    private ReaderController reader;
    private Runnable gpiPoll;

    private TextInputEditText serverField;
    private TextInputEditText gateField;
    private TextInputEditText deviceField;
    private TextInputEditText powerField;
    private TextInputEditText antennaField;
    private TextInputEditText alarmField;
    private TextInputEditText intervalField;
    private TextInputEditText noReadField;
    private TextInputEditText gpioInputField;
    private MaterialSwitch soundSwitch;
    private MaterialSwitch gpioStartSwitch;
    private MaterialSwitch gpioActiveHighSwitch;
    private TextView gpioLive;

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_settings);

        settings = new AppSettings(this);

        serverField = findViewById(R.id.server_url);
        gateField = findViewById(R.id.gate_code);
        deviceField = findViewById(R.id.device_id);
        powerField = findViewById(R.id.rf_power);
        antennaField = findViewById(R.id.antenna_port);
        alarmField = findViewById(R.id.alarm_output);
        intervalField = findViewById(R.id.read_interval);
        noReadField = findViewById(R.id.no_read_timeout);
        gpioInputField = findViewById(R.id.gpio_input);
        soundSwitch = findViewById(R.id.sound_enabled);
        gpioStartSwitch = findViewById(R.id.gpio_start);
        gpioActiveHighSwitch = findViewById(R.id.gpio_active_high);
        gpioLive = findViewById(R.id.gpio_live);

        serverField.setText(settings.serverUrl());
        gateField.setText(settings.gateCode());
        deviceField.setText(settings.deviceId());
        powerField.setText(String.valueOf(settings.powerDbm()));
        antennaField.setText(String.valueOf(settings.antennaPort()));
        alarmField.setText(String.valueOf(settings.alarmOutput()));
        intervalField.setText(String.valueOf(settings.readIntervalMs()));
        noReadField.setText(String.valueOf(settings.noReadTimeoutMs()));
        gpioInputField.setText(String.valueOf(settings.gpioInputPin()));
        soundSwitch.setChecked(settings.soundEnabled());
        gpioStartSwitch.setChecked(settings.gpioStartEnabled());
        gpioActiveHighSwitch.setChecked(settings.gpioActiveHigh());

        findViewById(R.id.save).setOnClickListener(v -> save());
        findViewById(R.id.back).setOnClickListener(v -> finish());
    }

    private void save() {
        String server = text(serverField);

        if (!TextUtils.isEmpty(server)
                && !server.startsWith("http://") && !server.startsWith("https://")) {
            server = "http://" + server;
        }

        settings.setServerUrl(server);
        settings.setGateCode(text(gateField));
        settings.setDeviceId(text(deviceField));
        settings.setPowerDbm(number(powerField, AppSettings.DEFAULT_POWER_DBM));
        settings.setAntennaPort(number(antennaField, AppSettings.DEFAULT_ANTENNA));
        settings.setAlarmOutput(number(alarmField, AppSettings.DEFAULT_ALARM_OUTPUT));
        settings.setReadIntervalMs(number(intervalField, AppSettings.DEFAULT_READ_INTERVAL_MS));
        settings.setNoReadTimeoutMs(number(noReadField, AppSettings.DEFAULT_NO_READ_TIMEOUT_MS));
        settings.setGpioInputPin(number(gpioInputField, AppSettings.DEFAULT_GPIO_INPUT));
        settings.setSoundEnabled(soundSwitch.isChecked());
        settings.setGpioStartEnabled(gpioStartSwitch.isChecked());
        settings.setGpioActiveHigh(gpioActiveHighSwitch.isChecked());

        // Values are clamped on the way in, so echo back what was actually kept.
        serverField.setText(settings.serverUrl());
        powerField.setText(String.valueOf(settings.powerDbm()));
        antennaField.setText(String.valueOf(settings.antennaPort()));
        alarmField.setText(String.valueOf(settings.alarmOutput()));
        intervalField.setText(String.valueOf(settings.readIntervalMs()));
        noReadField.setText(String.valueOf(settings.noReadTimeoutMs()));
        gpioInputField.setText(String.valueOf(settings.gpioInputPin()));

        Toast.makeText(this, R.string.settings_saved, Toast.LENGTH_SHORT).show();
        finish();
    }

    // ------------------------------------------------------------ live pins

    @Override
    protected void onResume() {
        super.onResume();

        gpioLive.setText(R.string.gpio_live_unavailable);

        // Opening the module can block, and only one program may hold it, so
        // it is opened here and released again the moment this screen closes.
        io.execute(() -> {
            reader = new ReaderController(this);

            if (reader.initialise()) {
                ui.post(this::startPolling);
            }
        });
    }

    @Override
    protected void onPause() {
        stopPolling();

        final ReaderController open = reader;
        reader = null;

        if (open != null) {
            io.execute(open::release);
        }

        super.onPause();
    }

    private void startPolling() {
        if (gpiPoll != null) {
            return;
        }

        gpiPoll = new Runnable() {
            @Override
            public void run() {
                ReaderController open = reader;

                if (open != null) {
                    gpioLive.setText(describe(open.inputLevels()));
                }

                ui.postDelayed(this, GPI_POLL_MS);
            }
        };

        ui.post(gpiPoll);
    }

    private void stopPolling() {
        if (gpiPoll != null) {
            ui.removeCallbacks(gpiPoll);
            gpiPoll = null;
        }
    }

    /** One line naming every pin and its level, with no pin assumed low. */
    private String describe(Map<String, Boolean> levels) {
        if (levels.isEmpty()) {
            return getString(R.string.gpio_live_unavailable);
        }

        StringBuilder line = new StringBuilder();

        for (Map.Entry<String, Boolean> pin : levels.entrySet()) {
            if (line.length() > 0) {
                line.append("   ");
            }

            line.append(pin.getKey())
                    .append(' ')
                    .append(getString(pin.getValue() ? R.string.gpio_on : R.string.gpio_off));
        }

        return line.toString();
    }

    @Override
    protected void onDestroy() {
        io.shutdown();
        super.onDestroy();
    }

    private static String text(TextInputEditText field) {
        return field.getText() == null ? "" : field.getText().toString().trim();
    }

    private static int number(TextInputEditText field, int fallback) {
        try {
            return Integer.parseInt(text(field));
        } catch (NumberFormatException e) {
            return fallback;
        }
    }
}
