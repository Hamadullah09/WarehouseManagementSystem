package com.smatechnology.denimrolls.rfid;

import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;
import android.util.Log;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * The three noises the gate makes, built here rather than taken from the SDK.
 *
 * <p>The reader offers exactly three sound calls -- {@code buzzer()},
 * {@code led()} and {@code successNotify()} -- with no control over pitch,
 * length or pattern, and on this unit an operator cannot tell one from
 * another across a warehouse. A roll that is wrong and a roll that is right
 * must not sound alike, so the tones are synthesised: a rising pair for a
 * roll that belongs, a low growl for one that does not, and a flat triple
 * blip for a roll that never answered.
 *
 * <p>Played on the alarm stream, which is the one stream a warehouse tends to
 * leave turned up. The reader's own buzzer and the beacon output still fire
 * for the two alarms, so the loud part of the alarm does not depend on the
 * panel speaker; these tones are what makes the three cases distinguishable.
 */
public final class Sounds {

    private static final String TAG = "Sounds";

    private static final int RATE = 44100;

    /** Fade at each end of a note. Square-edged tones click on this speaker. */
    private static final int FADE = 220;

    /** Rising pair: this roll is on the document. */
    private static final int[][] ACCEPTED = {{880, 90}, {0, 45}, {1320, 150}};

    /** Low growl: this roll is not on the document. */
    private static final int[][] WRONG = {{200, 320}, {0, 70}, {200, 320}};

    /** Flat triple blip: a roll went past and nothing answered. */
    private static final int[][] NO_TAG = {{560, 110}, {0, 90}, {560, 110}, {0, 90}, {560, 110}};

    private final ExecutorService player = Executors.newSingleThreadExecutor();

    public void accepted() {
        play(ACCEPTED);
    }

    public void wrongRoll() {
        play(WRONG);
    }

    public void noTag() {
        play(NO_TAG);
    }

    public void release() {
        player.shutdownNow();
    }

    /**
     * Plays one pattern.
     *
     * <p>Single-threaded on purpose: two alarms landing together queue rather
     * than overlap into noise nobody can identify.
     */
    private void play(int[][] pattern) {
        player.execute(() -> {
            try {
                short[] samples = render(pattern);
                AudioTrack track = build(samples.length * 2);

                track.write(samples, 0, samples.length);
                track.play();

                // Written before play(), so the buffer is complete; wait it
                // out before releasing or the tail is cut off.
                Thread.sleep(lengthMs(pattern) + 60L);

                track.stop();
                track.release();
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            } catch (Throwable t) {
                Log.w(TAG, "could not play a tone", t);
            }
        });
    }

    private AudioTrack build(int bytes) {
        return new AudioTrack.Builder()
                .setAudioAttributes(new AudioAttributes.Builder()
                        .setUsage(AudioAttributes.USAGE_ALARM)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                        .build())
                .setAudioFormat(new AudioFormat.Builder()
                        .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setSampleRate(RATE)
                        .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                        .build())
                .setBufferSizeInBytes(Math.max(bytes, AudioTrack.getMinBufferSize(
                        RATE, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)))
                .setTransferMode(AudioTrack.MODE_STATIC)
                .build();
    }

    private static short[] render(int[][] pattern) {
        short[] out = new short[(int) (RATE * lengthMs(pattern) / 1000L)];
        int at = 0;

        for (int[] note : pattern) {
            int frequency = note[0];
            int count = Math.min(RATE * note[1] / 1000, out.length - at);

            for (int i = 0; i < count; i++) {
                if (frequency > 0) {
                    double value = Math.sin(2 * Math.PI * frequency * i / RATE);
                    out[at + i] = (short) (value * Short.MAX_VALUE * 0.7 * taper(i, count));
                }
            }

            at += count;
        }

        return out;
    }

    /** 0..1 ramp at both ends of a note, so it starts and stops cleanly. */
    private static double taper(int i, int count) {
        if (i < FADE) {
            return i / (double) FADE;
        }

        if (i > count - FADE) {
            return Math.max(0, count - i) / (double) FADE;
        }

        return 1;
    }

    private static long lengthMs(int[][] pattern) {
        long total = 0;

        for (int[] note : pattern) {
            total += note[1];
        }

        return total;
    }

    /** Volume of the alarm stream, so a silenced device can be reported. */
    public static boolean audible(AudioManager audio) {
        return audio != null && audio.getStreamVolume(AudioManager.STREAM_ALARM) > 0;
    }
}
