package com.smatechnology.denimrolls.data;

import android.content.Context;
import android.content.SharedPreferences;
import android.text.TextUtils;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/**
 * Client for the warehouse API.
 *
 * <p>Built on {@link HttpURLConnection} and {@code org.json}, both part of the
 * platform. A reader mounted above a loading bay may go years between
 * software updates, and every third-party networking library added here is one
 * more thing that has to be patched on it.
 *
 * <p>Errors from the server arrive as RFC 7807 problem documents. The
 * {@code detail} field is written for a person, so {@link ApiException} carries
 * it through to the screen rather than replacing it with a status code.
 */
public final class ApiClient {

    private static final String TAG = "ApiClient";
    private static final int CONNECT_TIMEOUT_MS = 8000;
    private static final int READ_TIMEOUT_MS = 20000;

    private static final String SESSION_FILE = "denim-rolls-session";
    private static final String KEY_TOKEN = "token";
    private static final String KEY_USER = "user";
    private static final String KEY_DISPLAY = "display";
    private static final String KEY_ROLES = "roles";

    private final AppSettings settings;
    private final SharedPreferences session;

    public ApiClient(Context context) {
        this.settings = new AppSettings(context);
        this.session = context.getApplicationContext()
                .getSharedPreferences(SESSION_FILE, Context.MODE_PRIVATE);
    }

    // ------------------------------------------------------------- session

    public String token() {
        return session.getString(KEY_TOKEN, "");
    }

    public String displayName() {
        return session.getString(KEY_DISPLAY, "");
    }

    public String userName() {
        return session.getString(KEY_USER, "");
    }

    public String roles() {
        return session.getString(KEY_ROLES, "");
    }

    public boolean isSignedIn() {
        return !TextUtils.isEmpty(token());
    }

    public void signOut() {
        session.edit().clear().apply();
    }

    /** Authenticates and stores the token. Throws on bad credentials. */
    public void signIn(String user, String password) throws ApiException {
        JSONObject body = new JSONObject();

        try {
            body.put("userName", user);
            body.put("password", password);
        } catch (JSONException e) {
            throw new ApiException("Could not build the sign-in request.", e);
        }

        JSONObject response = requestObject("POST", "/api/auth/login", body, false);

        StringBuilder roles = new StringBuilder();
        JSONArray array = response.optJSONArray("roles");

        for (int i = 0; array != null && i < array.length(); i++) {
            if (roles.length() > 0) {
                roles.append(", ");
            }

            roles.append(array.optString(i));
        }

        session.edit()
                .putString(KEY_TOKEN, response.optString("token"))
                .putString(KEY_USER, response.optString("userName"))
                .putString(KEY_DISPLAY, response.optString("displayName"))
                .putString(KEY_ROLES, roles.toString())
                .apply();
    }

    // ----------------------------------------------------------- documents

    /**
     * Lists documents available to work. Filters are optional; passing null
     * for both returns everything the server will show.
     */
    public List<DocumentSummary> listDocuments(String type, String status) throws ApiException {
        StringBuilder path = new StringBuilder("/api/documents?pageSize=200");

        if (!TextUtils.isEmpty(type)) {
            path.append("&type=").append(encode(type));
        }

        if (!TextUtils.isEmpty(status)) {
            path.append("&status=").append(encode(status));
        }

        JSONObject page = requestObject("GET", path.toString(), null, true);
        JSONArray items = page.optJSONArray("items");
        List<DocumentSummary> result = new ArrayList<>();

        for (int i = 0; items != null && i < items.length(); i++) {
            result.add(DocumentSummary.from(items.optJSONObject(i)));
        }

        return result;
    }

    /** Fetches one document with its full EPC list. */
    public DocumentDetail getDocument(int id) throws ApiException {
        return DocumentDetail.from(requestObject("GET", "/api/documents/" + id, null, true));
    }

    /** Fetches one document by its printed number, e.g. IN-2026-000001. */
    public DocumentDetail getDocumentByNumber(String number) throws ApiException {
        return DocumentDetail.from(
                requestObject("GET", "/api/documents/by-number/" + encode(number), null, true));
    }

    /**
     * Submits a completed read session. The server re-validates and is the only
     * thing that moves stock; the sessionKey makes a retry safe.
     */
    public SessionResult submitSession(
            int documentId,
            List<String> epcs,
            int rawReadCount,
            boolean readerHealthy,
            String sessionKey,
            String startedAtIso,
            String completedAtIso) throws ApiException {

        JSONObject body = new JSONObject();

        try {
            body.put("gateCode", settings.gateCode());
            body.put("deviceId", settings.deviceId());
            body.put("detectedEpcs", new JSONArray(epcs));
            body.put("rawReadCount", rawReadCount);
            body.put("readerHealthy", readerHealthy);
            body.put("sessionKey", sessionKey);

            if (startedAtIso != null) {
                body.put("startedAt", startedAtIso);
            }

            if (completedAtIso != null) {
                body.put("completedAt", completedAtIso);
            }
        } catch (JSONException e) {
            throw new ApiException("Could not build the session request.", e);
        }

        return SessionResult.from(
                requestObject("POST", "/api/documents/" + documentId + "/scan-sessions", body, true));
    }

    // --------------------------------------------------------------- account

    /** Asks an administrator to set a new password. Always answers the same way. */
    public String forgotPassword(String user) throws ApiException {
        JSONObject body = new JSONObject();

        try {
            body.put("userName", user);
        } catch (JSONException e) {
            throw new ApiException("Could not build the request.", e);
        }

        JSONObject response = requestObject("POST", "/api/auth/forgot-password", body, false);

        return response.optString("message",
                "Your supervisor has been asked to set a new password for you.");
    }

    /** Changes the signed-in user's own password. */
    public void changePassword(String current, String replacement) throws ApiException {
        JSONObject body = new JSONObject();

        try {
            body.put("currentPassword", current);
            body.put("newPassword", replacement);
        } catch (JSONException e) {
            throw new ApiException("Could not build the request.", e);
        }

        request("POST", "/api/auth/change-password", body, true);
    }

    // ----------------------------------------------------------------- users

    public List<WarehouseUser> listUsers() throws ApiException {
        JSONArray array = requestArray("GET", "/api/users");
        List<WarehouseUser> users = new ArrayList<>();

        for (int i = 0; array != null && i < array.length(); i++) {
            users.add(WarehouseUser.from(array.optJSONObject(i)));
        }

        return users;
    }

    public List<String> listRoles() throws ApiException {
        JSONArray array = requestArray("GET", "/api/users/roles");
        List<String> roles = new ArrayList<>();

        for (int i = 0; array != null && i < array.length(); i++) {
            JSONObject role = array.optJSONObject(i);

            if (role != null) {
                roles.add(role.optString("name"));
            }
        }

        return roles;
    }

    public void createUser(String user, String displayName, String email,
                           String password, String role) throws ApiException {
        JSONObject body = new JSONObject();

        try {
            body.put("userName", user);
            body.put("displayName", displayName);
            body.put("email", email);
            body.put("password", password);
            body.put("roles", new JSONArray().put(role));
            body.put("mustChangePassword", true);
        } catch (JSONException e) {
            throw new ApiException("Could not build the request.", e);
        }

        request("POST", "/api/users", body, true);
    }

    public void updateUser(int id, String displayName, String email,
                           String role, boolean active) throws ApiException {
        JSONObject body = new JSONObject();

        try {
            body.put("displayName", displayName);
            body.put("email", email);
            body.put("roles", new JSONArray().put(role));
            body.put("isActive", active);
        } catch (JSONException e) {
            throw new ApiException("Could not build the request.", e);
        }

        request("PUT", "/api/users/" + id, body, true);
    }

    public void resetUserPassword(int id, String password) throws ApiException {
        JSONObject body = new JSONObject();

        try {
            body.put("newPassword", password);
            body.put("mustChangePassword", true);
        } catch (JSONException e) {
            throw new ApiException("Could not build the request.", e);
        }

        request("POST", "/api/users/" + id + "/reset-password", body, true);
    }

    /** Removes an account, or deactivates it when it has history. Returns what happened. */
    public String deleteUser(int id) throws ApiException {
        String text = request("DELETE", "/api/users/" + id, null, true);

        try {
            return TextUtils.isEmpty(text)
                    ? "Account removed."
                    : new JSONObject(text).optString("message", "Account removed.");
        } catch (JSONException e) {
            return "Account removed.";
        }
    }

    private JSONArray requestArray(String method, String path) throws ApiException {
        String text = request(method, path, null, true);

        try {
            return TextUtils.isEmpty(text) ? new JSONArray() : new JSONArray(text);
        } catch (JSONException e) {
            throw new ApiException("The server returned a list the app could not read.", e);
        }
    }

    // -------------------------------------------------------------- plumbing

    private JSONObject requestObject(String method, String path, JSONObject body, boolean authenticated)
            throws ApiException {

        String text = request(method, path, body, authenticated);

        try {
            return TextUtils.isEmpty(text) ? new JSONObject() : new JSONObject(text);
        } catch (JSONException e) {
            throw new ApiException("The server returned a response the app could not read.", e);
        }
    }

    private String request(String method, String path, JSONObject body, boolean authenticated)
            throws ApiException {

        String base = settings.serverUrl();

        if (TextUtils.isEmpty(base)) {
            throw new ApiException("No server address is configured. Open Settings and enter one.");
        }

        HttpURLConnection connection = null;

        try {
            connection = (HttpURLConnection) new URL(base + path).openConnection();
            connection.setRequestMethod(method);
            connection.setConnectTimeout(CONNECT_TIMEOUT_MS);
            connection.setReadTimeout(READ_TIMEOUT_MS);
            connection.setRequestProperty("Accept", "application/json");

            if (authenticated) {
                if (!isSignedIn()) {
                    throw new ApiException("You are signed out. Sign in again.");
                }

                connection.setRequestProperty("Authorization", "Bearer " + token());
            }

            if (body != null) {
                connection.setDoOutput(true);
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");

                byte[] payload = body.toString().getBytes(StandardCharsets.UTF_8);

                try (OutputStream out = connection.getOutputStream()) {
                    out.write(payload);
                }
            }

            int status = connection.getResponseCode();

            if (status >= 200 && status < 300) {
                return read(connection.getInputStream());
            }

            String error = read(connection.getErrorStream());

            if (status == 401) {
                signOut();
                throw new ApiException("Your session has expired. Sign in again.");
            }

            throw new ApiException(describe(status, error));
        } catch (ApiException e) {
            throw e;
        } catch (IOException e) {
            Log.w(TAG, method + " " + path + " failed", e);
            throw new ApiException("Cannot reach the server at " + base + ". Check the network and the address.", e);
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
    }

    /** Prefers the server's own explanation over an HTTP status code. */
    private static String describe(int status, String payload) {
        if (!TextUtils.isEmpty(payload)) {
            try {
                JSONObject problem = new JSONObject(payload);
                String detail = problem.optString("detail", "");

                if (!TextUtils.isEmpty(detail)) {
                    JSONArray offending = problem.optJSONArray("offending");

                    if (offending != null && offending.length() > 0) {
                        StringBuilder sb = new StringBuilder(detail);
                        sb.append("\n");

                        for (int i = 0; i < Math.min(5, offending.length()); i++) {
                            sb.append("\n").append(offending.optString(i));
                        }

                        if (offending.length() > 5) {
                            sb.append("\n+").append(offending.length() - 5).append(" more");
                        }

                        return sb.toString();
                    }

                    return detail;
                }
            } catch (JSONException ignored) {
                // Not a problem document; fall through to the generic message.
            }
        }

        return String.format(Locale.US, "The server rejected the request (HTTP %d).", status);
    }

    private static String read(InputStream stream) throws IOException {
        if (stream == null) {
            return "";
        }

        StringBuilder sb = new StringBuilder();

        try (BufferedReader reader = new BufferedReader(
                new InputStreamReader(stream, StandardCharsets.UTF_8))) {

            String line;

            while ((line = reader.readLine()) != null) {
                sb.append(line);
            }
        }

        return sb.toString();
    }

    private static String encode(String value) {
        try {
            return URLEncoder.encode(value, "UTF-8");
        } catch (IOException e) {
            return value;
        }
    }

    /** Carries a message already fit to show an operator. */
    public static final class ApiException extends Exception {
        public ApiException(String message) {
            super(message);
        }

        public ApiException(String message, Throwable cause) {
            super(message, cause);
        }
    }
}
