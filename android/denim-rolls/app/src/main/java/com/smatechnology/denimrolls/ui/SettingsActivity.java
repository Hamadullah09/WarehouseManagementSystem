package com.smatechnology.denimrolls.ui;

import android.os.Bundle;
import android.text.TextUtils;
import android.widget.Toast;

import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;

import com.google.android.material.materialswitch.MaterialSwitch;
import com.google.android.material.textfield.TextInputEditText;
import com.smatechnology.denimrolls.R;
import com.smatechnology.denimrolls.data.AppSettings;

/**
 * Deployment settings.
 *
 * <p>This screen exists so the APK is identical in every warehouse. Server
 * address, gate, read pace, RF power and which optocoupler drives the beacon
 * are all site facts, and none of them is compiled in.
 */
public final class SettingsActivity extends AppCompatActivity {

    private AppSettings settings;

    private TextInputEditText serverField;
    private TextInputEditText gateField;
    private TextInputEditText deviceField;
    private TextInputEditText intervalField;
    private TextInputEditText powerField;
    private TextInputEditText antennaField;
    private TextInputEditText alarmField;
    private MaterialSwitch soundSwitch;

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_settings);

        settings = new AppSettings(this);

        serverField = findViewById(R.id.server_url);
        gateField = findViewById(R.id.gate_code);
        deviceField = findViewById(R.id.device_id);
        intervalField = findViewById(R.id.read_interval);
        powerField = findViewById(R.id.rf_power);
        antennaField = findViewById(R.id.antenna_port);
        alarmField = findViewById(R.id.alarm_output);
        soundSwitch = findViewById(R.id.sound_enabled);

        serverField.setText(settings.serverUrl());
        gateField.setText(settings.gateCode());
        deviceField.setText(settings.deviceId());
        intervalField.setText(String.valueOf(settings.readIntervalMs()));
        powerField.setText(String.valueOf(settings.powerDbm()));
        antennaField.setText(String.valueOf(settings.antennaPort()));
        alarmField.setText(String.valueOf(settings.alarmOutput()));
        soundSwitch.setChecked(settings.soundEnabled());

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
        settings.setReadIntervalMs(number(intervalField, AppSettings.DEFAULT_READ_INTERVAL_MS));
        settings.setPowerDbm(number(powerField, AppSettings.DEFAULT_POWER_DBM));
        settings.setAntennaPort(number(antennaField, AppSettings.DEFAULT_ANTENNA));
        settings.setAlarmOutput(number(alarmField, AppSettings.DEFAULT_ALARM_OUTPUT));
        settings.setSoundEnabled(soundSwitch.isChecked());

        // Values are clamped on the way in, so echo back what was actually kept.
        serverField.setText(settings.serverUrl());
        powerField.setText(String.valueOf(settings.powerDbm()));
        antennaField.setText(String.valueOf(settings.antennaPort()));
        alarmField.setText(String.valueOf(settings.alarmOutput()));

        Toast.makeText(this, R.string.settings_saved, Toast.LENGTH_SHORT).show();
        finish();
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
