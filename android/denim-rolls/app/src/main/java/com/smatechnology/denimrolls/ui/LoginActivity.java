package com.smatechnology.denimrolls.ui;

import android.content.Intent;
import android.os.Bundle;
import android.text.TextUtils;
import android.view.View;

import androidx.annotation.Nullable;
import androidx.appcompat.app.AppCompatActivity;

import com.google.android.material.button.MaterialButton;
import com.google.android.material.progressindicator.CircularProgressIndicator;
import com.google.android.material.textfield.TextInputEditText;
import com.smatechnology.denimrolls.R;
import com.smatechnology.denimrolls.data.ApiClient;
import com.smatechnology.denimrolls.data.AppSettings;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Sign-in.
 *
 * <p>Every movement this app records is attributed to the person signed in
 * here, so it is the first screen and there is no way past it. If the device
 * has never been configured, it sends the operator to Settings first: a
 * password typed against a blank server address only produces a confusing
 * failure.
 */
public final class LoginActivity extends AppCompatActivity {

    private final ExecutorService io = Executors.newSingleThreadExecutor();

    private ApiClient api;
    private AppSettings settings;

    private TextInputEditText userField;
    private TextInputEditText passwordField;
    private MaterialButton signInButton;
    private CircularProgressIndicator progress;
    private View errorBanner;
    private android.widget.TextView errorText;
    private android.widget.TextView targetText;

    @Override
    protected void onCreate(@Nullable Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_login);

        api = new ApiClient(this);
        settings = new AppSettings(this);

        userField = findViewById(R.id.user_name);
        passwordField = findViewById(R.id.password);
        signInButton = findViewById(R.id.sign_in);
        progress = findViewById(R.id.progress);
        errorBanner = findViewById(R.id.error_banner);
        errorText = findViewById(R.id.error_text);
        targetText = findViewById(R.id.target);

        signInButton.setOnClickListener(v -> attemptSignIn());
        findViewById(R.id.settings).setOnClickListener(
                v -> startActivity(new Intent(this, SettingsActivity.class)));

        userField.setText(api.userName());
    }

    @Override
    protected void onResume() {
        super.onResume();

        if (settings.isConfigured()) {
            targetText.setText(getString(
                    R.string.login_target, settings.gateCode(), settings.serverUrl()));
        } else {
            targetText.setText(R.string.login_unconfigured);
        }

        // A live token means the operator is already signed in.
        if (api.isSignedIn()) {
            openDocuments();
        }
    }

    private void attemptSignIn() {
        String user = text(userField);
        String password = text(passwordField);

        hideError();

        if (!settings.isConfigured()) {
            showError(getString(R.string.login_configure_first));
            startActivity(new Intent(this, SettingsActivity.class));

            return;
        }

        if (TextUtils.isEmpty(user) || TextUtils.isEmpty(password)) {
            showError(getString(R.string.login_missing_credentials));

            return;
        }

        setBusy(true);

        io.execute(() -> {
            try {
                api.signIn(user, password);
                runOnUiThread(() -> {
                    setBusy(false);
                    passwordField.setText("");
                    openDocuments();
                });
            } catch (ApiClient.ApiException e) {
                runOnUiThread(() -> {
                    setBusy(false);
                    showError(e.getMessage());
                });
            }
        });
    }

    private void openDocuments() {
        startActivity(new Intent(this, DocumentsActivity.class));
        finish();
    }

    private void setBusy(boolean busy) {
        progress.setVisibility(busy ? View.VISIBLE : View.GONE);
        signInButton.setEnabled(!busy);
        userField.setEnabled(!busy);
        passwordField.setEnabled(!busy);
    }

    private void showError(String message) {
        errorText.setText(message);
        errorBanner.setVisibility(View.VISIBLE);
    }

    private void hideError() {
        errorBanner.setVisibility(View.GONE);
    }

    private static String text(TextInputEditText field) {
        return field.getText() == null ? "" : field.getText().toString().trim();
    }

    @Override
    protected void onDestroy() {
        io.shutdownNow();
        super.onDestroy();
    }
}
