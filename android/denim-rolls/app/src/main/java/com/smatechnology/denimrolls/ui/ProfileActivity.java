package com.smatechnology.denimrolls.ui;

import android.os.Bundle;
import android.text.TextUtils;
import android.view.View;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;

import com.google.android.material.textfield.TextInputEditText;
import com.smatechnology.denimrolls.R;
import com.smatechnology.denimrolls.data.ApiClient;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * The signed-in person's own details, and the one thing they can change here.
 *
 * <p>Nothing about the deployment is on this screen. An operator has no reason
 * to see a server address, and every reason to be able to change their own
 * password without finding a supervisor.
 */
public final class ProfileActivity extends AppCompatActivity {

    private final ExecutorService io = Executors.newSingleThreadExecutor();

    private ApiClient api;
    private TextInputEditText currentField;
    private TextInputEditText newField;
    private TextInputEditText confirmField;
    private View errorBanner;
    private TextView errorText;

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_profile);

        api = new ApiClient(this);

        ((TextView) findViewById(R.id.name)).setText(api.displayName());
        ((TextView) findViewById(R.id.username)).setText(api.userName());
        ((TextView) findViewById(R.id.roles)).setText(api.roles());

        currentField = findViewById(R.id.current_password);
        newField = findViewById(R.id.new_password);
        confirmField = findViewById(R.id.confirm_password);
        errorBanner = findViewById(R.id.error_banner);
        errorText = findViewById(R.id.error_text);

        findViewById(R.id.save).setOnClickListener(v -> change());
        findViewById(R.id.back).setOnClickListener(v -> finish());
    }

    private void change() {
        String current = text(currentField);
        String replacement = text(newField);
        String confirm = text(confirmField);

        errorBanner.setVisibility(View.GONE);

        if (TextUtils.isEmpty(current) || TextUtils.isEmpty(replacement)) {
            show(getString(R.string.login_missing_credentials));
            return;
        }

        if (!replacement.equals(confirm)) {
            show(getString(R.string.password_mismatch));
            return;
        }

        io.execute(() -> {
            try {
                api.changePassword(current, replacement);

                runOnUiThread(() -> {
                    Toast.makeText(this, R.string.password_changed, Toast.LENGTH_LONG).show();
                    finish();
                });
            } catch (ApiClient.ApiException e) {
                runOnUiThread(() -> show(e.getMessage()));
            }
        });
    }

    private void show(String message) {
        errorText.setText(message);
        errorBanner.setVisibility(View.VISIBLE);
    }

    private static String text(TextInputEditText field) {
        return field.getText() == null ? "" : field.getText().toString();
    }

    @Override
    protected void onDestroy() {
        io.shutdownNow();
        super.onDestroy();
    }
}
