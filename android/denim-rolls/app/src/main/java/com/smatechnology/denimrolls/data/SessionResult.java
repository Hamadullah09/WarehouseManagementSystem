package com.smatechnology.denimrolls.data;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

/** The verdict the server returns for a completed read session. */
public final class SessionResult {

    public String cycleId = "";
    public boolean passed;
    public String summary = "";
    public String documentNumber = "";
    public String documentStatus = "";
    public int expectedCount;
    public int detectedCount;
    public int movedArticles;
    public int movedQuantity;
    public int balanceArticles;
    public int balanceQuantity;
    public boolean wasReplay;

    public final List<String> missing = new ArrayList<>();
    public final List<String> unknown = new ArrayList<>();
    public final List<String> unexpected = new ArrayList<>();
    public final List<String> alarms = new ArrayList<>();

    public static SessionResult from(JSONObject json) {
        SessionResult r = new SessionResult();

        if (json == null) {
            return r;
        }

        r.cycleId = json.optString("cycleId", "");
        r.passed = json.optBoolean("passed");
        r.summary = json.optString("summary", "");
        r.documentNumber = json.optString("documentNumber", "");
        r.documentStatus = json.optString("documentStatus", "");
        r.expectedCount = json.optInt("expectedCount");
        r.detectedCount = json.optInt("detectedCount");
        r.movedArticles = json.optInt("movedArticles");
        r.movedQuantity = json.optInt("movedQuantity");
        r.balanceArticles = json.optInt("balanceArticles");
        r.balanceQuantity = json.optInt("balanceQuantity");
        r.wasReplay = json.optBoolean("wasReplay");

        fill(json.optJSONArray("missing"), r.missing);
        fill(json.optJSONArray("unknown"), r.unknown);
        fill(json.optJSONArray("unexpected"), r.unexpected);
        fill(json.optJSONArray("alarms"), r.alarms);

        return r;
    }

    private static void fill(JSONArray array, List<String> target) {
        for (int i = 0; array != null && i < array.length(); i++) {
            target.add(array.optString(i));
        }
    }
}
