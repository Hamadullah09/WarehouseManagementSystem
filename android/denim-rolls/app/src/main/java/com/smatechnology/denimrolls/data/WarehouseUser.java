package com.smatechnology.denimrolls.data;

import org.json.JSONArray;
import org.json.JSONObject;

/** An account, as the administration screen shows it. */
public final class WarehouseUser {

    public int id;
    public String userName = "";
    public String displayName = "";
    public String email = "";
    public String role = "";
    public boolean isActive;
    public boolean mustChangePassword;
    public boolean isLockedOut;

    /** True when this person has asked for a new password. */
    public boolean resetRequested;

    public static WarehouseUser from(JSONObject json) {
        WarehouseUser u = new WarehouseUser();

        if (json == null) {
            return u;
        }

        u.id = json.optInt("id");
        u.userName = json.optString("userName", "");
        u.displayName = json.optString("displayName", "");
        u.email = json.optString("email", "");
        u.isActive = json.optBoolean("isActive");
        u.mustChangePassword = json.optBoolean("mustChangePassword");
        u.isLockedOut = json.optBoolean("isLockedOut");
        u.resetRequested = json.optBoolean("resetRequested");

        JSONArray roles = json.optJSONArray("roles");
        u.role = roles != null && roles.length() > 0 ? roles.optString(0) : "";

        return u;
    }

    /** What the list should say about this account, in plain words. */
    public String statusLine() {
        if (!isActive) {
            return "Switched off";
        }

        if (isLockedOut) {
            return "Locked out";
        }

        if (resetRequested) {
            return "Waiting for a new password";
        }

        if (mustChangePassword) {
            return "Must set a password";
        }

        return "Ready";
    }
}
