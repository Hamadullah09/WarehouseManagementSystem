package com.warehouse.u300bridge;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Minimal JSON reader/writer for the bridge protocol.
 *
 * <p>Deliberately dependency-free. The protocol is small, fixed and generated
 * by code at both ends, so a full JSON library would add jars to ship and
 * version conflicts to debug for no benefit. Values map to {@code Map},
 * {@code List}, {@code String}, {@code Double}, {@code Boolean} and null.
 */
final class Json {

    private final String src;
    private int pos;

    private Json(String src) {
        this.src = src;
    }

    static Object parse(String text) {
        Json p = new Json(text);
        p.skipWhitespace();
        Object value = p.readValue();
        p.skipWhitespace();

        if (p.pos < p.src.length()) {
            throw new IllegalArgumentException("Trailing content at offset " + p.pos);
        }

        return value;
    }

    @SuppressWarnings("unchecked")
    static Map<String, Object> parseObject(String text) {
        Object value = parse(text);

        if (!(value instanceof Map)) {
            throw new IllegalArgumentException("Expected a JSON object");
        }

        return (Map<String, Object>) value;
    }

    // ------------------------------------------------------------- reading

    private Object readValue() {
        if (pos >= src.length()) {
            throw new IllegalArgumentException("Unexpected end of input");
        }

        char c = src.charAt(pos);

        switch (c) {
            case '{': return readObject();
            case '[': return readArray();
            case '"': return readString();
            case 't': expect("true"); return Boolean.TRUE;
            case 'f': expect("false"); return Boolean.FALSE;
            case 'n': expect("null"); return null;
            default: return readNumber();
        }
    }

    private Map<String, Object> readObject() {
        Map<String, Object> map = new LinkedHashMap<>();
        pos++; // '{'
        skipWhitespace();

        if (peek() == '}') {
            pos++;
            return map;
        }

        while (true) {
            skipWhitespace();
            String key = readString();
            skipWhitespace();

            if (peek() != ':') {
                throw new IllegalArgumentException("Expected ':' at offset " + pos);
            }

            pos++;
            skipWhitespace();
            map.put(key, readValue());
            skipWhitespace();

            char c = peek();
            pos++;

            if (c == '}') {
                return map;
            }

            if (c != ',') {
                throw new IllegalArgumentException("Expected ',' or '}' at offset " + (pos - 1));
            }
        }
    }

    private List<Object> readArray() {
        List<Object> list = new ArrayList<>();
        pos++; // '['
        skipWhitespace();

        if (peek() == ']') {
            pos++;
            return list;
        }

        while (true) {
            skipWhitespace();
            list.add(readValue());
            skipWhitespace();

            char c = peek();
            pos++;

            if (c == ']') {
                return list;
            }

            if (c != ',') {
                throw new IllegalArgumentException("Expected ',' or ']' at offset " + (pos - 1));
            }
        }
    }

    private String readString() {
        if (peek() != '"') {
            throw new IllegalArgumentException("Expected '\"' at offset " + pos);
        }

        pos++;
        StringBuilder sb = new StringBuilder();

        while (true) {
            if (pos >= src.length()) {
                throw new IllegalArgumentException("Unterminated string");
            }

            char c = src.charAt(pos++);

            if (c == '"') {
                return sb.toString();
            }

            if (c != '\\') {
                sb.append(c);
                continue;
            }

            char esc = src.charAt(pos++);

            switch (esc) {
                case '"':  sb.append('"');  break;
                case '\\': sb.append('\\'); break;
                case '/':  sb.append('/');  break;
                case 'b':  sb.append('\b'); break;
                case 'f':  sb.append('\f'); break;
                case 'n':  sb.append('\n'); break;
                case 'r':  sb.append('\r'); break;
                case 't':  sb.append('\t'); break;
                case 'u':
                    sb.append((char) Integer.parseInt(src.substring(pos, pos + 4), 16));
                    pos += 4;
                    break;
                default:
                    throw new IllegalArgumentException("Bad escape \\" + esc);
            }
        }
    }

    private Double readNumber() {
        int start = pos;

        while (pos < src.length() && "-+.eE0123456789".indexOf(src.charAt(pos)) >= 0) {
            pos++;
        }

        if (start == pos) {
            throw new IllegalArgumentException("Expected a value at offset " + pos);
        }

        return Double.valueOf(src.substring(start, pos));
    }

    private char peek() {
        if (pos >= src.length()) {
            throw new IllegalArgumentException("Unexpected end of input");
        }

        return src.charAt(pos);
    }

    private void expect(String literal) {
        if (!src.startsWith(literal, pos)) {
            throw new IllegalArgumentException("Expected '" + literal + "' at offset " + pos);
        }

        pos += literal.length();
    }

    private void skipWhitespace() {
        while (pos < src.length() && Character.isWhitespace(src.charAt(pos))) {
            pos++;
        }
    }

    // ------------------------------------------------------------- writing

    static String write(Object value) {
        StringBuilder sb = new StringBuilder(256);
        writeTo(sb, value);
        return sb.toString();
    }

    private static void writeTo(StringBuilder sb, Object value) {
        if (value == null) {
            sb.append("null");
        } else if (value instanceof String) {
            writeString(sb, (String) value);
        } else if (value instanceof Boolean) {
            sb.append(value);
        } else if (value instanceof Number) {
            double d = ((Number) value).doubleValue();

            if (d == Math.rint(d) && !Double.isInfinite(d) && Math.abs(d) < 1e15) {
                sb.append((long) d);
            } else {
                sb.append(d);
            }
        } else if (value instanceof Map) {
            sb.append('{');
            boolean first = true;

            for (Map.Entry<?, ?> e : ((Map<?, ?>) value).entrySet()) {
                if (!first) {
                    sb.append(',');
                }

                first = false;
                writeString(sb, String.valueOf(e.getKey()));
                sb.append(':');
                writeTo(sb, e.getValue());
            }

            sb.append('}');
        } else if (value instanceof Iterable) {
            sb.append('[');
            boolean first = true;

            for (Object item : (Iterable<?>) value) {
                if (!first) {
                    sb.append(',');
                }

                first = false;
                writeTo(sb, item);
            }

            sb.append(']');
        } else {
            writeString(sb, String.valueOf(value));
        }
    }

    private static void writeString(StringBuilder sb, String s) {
        sb.append('"');

        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);

            switch (c) {
                case '"':  sb.append("\\\""); break;
                case '\\': sb.append("\\\\"); break;
                case '\b': sb.append("\\b");  break;
                case '\f': sb.append("\\f");  break;
                case '\n': sb.append("\\n");  break;
                case '\r': sb.append("\\r");  break;
                case '\t': sb.append("\\t");  break;
                default:
                    if (c < 0x20) {
                        sb.append(String.format("\\u%04x", (int) c));
                    } else {
                        sb.append(c);
                    }
            }
        }

        sb.append('"');
    }

    // ------------------------------------------------------------- helpers

    static String str(Map<String, Object> map, String key) {
        Object value = map.get(key);
        return value == null ? null : String.valueOf(value);
    }

    static int intOf(Map<String, Object> map, String key, int fallback) {
        Object value = map.get(key);
        return value instanceof Number ? ((Number) value).intValue() : fallback;
    }

    static boolean boolOf(Map<String, Object> map, String key, boolean fallback) {
        Object value = map.get(key);
        return value instanceof Boolean ? (Boolean) value : fallback;
    }

    static Map<String, Object> obj() {
        return new LinkedHashMap<>();
    }
}
