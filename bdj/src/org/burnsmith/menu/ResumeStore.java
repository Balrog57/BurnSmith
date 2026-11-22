package org.burnsmith.menu;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;

/** A title and its position in milliseconds, scoped to one authored disc. */
final class ResumeStore {
    private final File file;

    ResumeStore(String key) {
        if (key.length() != 32) throw new IllegalArgumentException("resume key length");
        for (int i = 0; i < key.length(); i++)
            if ("0123456789abcdef".indexOf(key.charAt(i)) < 0) throw new IllegalArgumentException("resume key");
        String root = System.getProperty("dvb.persistent.root");
        if (root == null || root.length() == 0) throw new IllegalStateException("persistent storage unavailable");
        file = new File(new File(new File(root, "42535430"), "4001"), key + ".resume");
    }

    long read() throws IOException {
        if (!file.isFile()) return 0;
        RandomAccessFile input = new RandomAccessFile(file, "r");
        try {
            if (input.length() != 16) return 0;
            long value = input.readLong();
            return input.readLong() == ~value ? value : 0;
        } finally { input.close(); }
    }

    void write(long value) throws IOException {
        File dir = file.getParentFile();
        if (!dir.isDirectory() && !dir.mkdirs() && !dir.isDirectory()) throw new IOException("resume directory unavailable");
        RandomAccessFile output = new RandomAccessFile(file, "rw");
        try {
            output.writeLong(value);
            output.writeLong(~value);
        } finally { output.close(); }
    }
}
