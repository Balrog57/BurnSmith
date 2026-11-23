package org.burnsmith.menu;

import java.awt.Container;
import java.awt.Color;
import java.awt.Graphics;
import java.awt.Image;
import java.awt.MediaTracker;
import java.awt.Toolkit;
import java.awt.event.KeyEvent;
import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.util.Hashtable;
import java.util.StringTokenizer;
import java.util.Timer;
import java.util.TimerTask;
import java.util.Vector;

import javax.media.Control;
import javax.media.Controller;
import javax.media.ControllerErrorEvent;
import javax.media.ControllerEvent;
import javax.media.ControllerListener;
import javax.media.EndOfMediaEvent;
import javax.media.Manager;
import javax.media.MediaLocator;
import javax.media.Player;
import javax.media.StartEvent;
import javax.media.Time;
import javax.tv.service.SIManager;
import javax.tv.service.selection.ServiceContextFactory;
import javax.tv.xlet.Xlet;
import javax.tv.xlet.XletContext;
import javax.tv.xlet.XletStateChangeException;

import org.bluray.media.PlaybackControl;
import org.bluray.media.PlaybackListener;
import org.bluray.media.PlaybackMarkEvent;
import org.bluray.media.PlaybackPlayItemEvent;
import org.bluray.media.PrimaryAudioControl;
import org.bluray.media.SubtitlingControl;
import org.bluray.net.BDLocator;
import org.bluray.system.RegisterAccess;
import org.dvb.event.EventManager;
import org.havi.ui.HSound;
import org.dvb.event.UserEvent;
import org.dvb.event.UserEventListener;
import org.dvb.event.UserEventRepository;
import org.dvb.ui.DVBAlphaComposite;
import org.dvb.ui.DVBGraphics;
import org.havi.ui.HScene;
import org.havi.ui.HSceneFactory;
import org.havi.ui.event.HRcEvent;

/**
 * Generic BD-J menu. Draws the pages described by /menu.txt (inside the jar) and runs button actions.
 * Source level 1.3 on purpose: this is the bytecode level every Blu-ray player JVM accepts.
 *
 * menu.txt, one directive per line, "-" means none, '#' starts a comment:
 *   size W H
 *   page ID BACKGROUND_IMAGE [MARKER_DX]   (MARKER_DX: this page's own marker offset, else the marker line's)
 *   button PAGE ID X Y W H FOCUS_IMAGE LEFT RIGHT UP DOWN ACTION [ARG1 [ARG2]]
 *   focus-frames IMAGE IMAGE ...; focus-slot X Y W H (for the preceding button)
 *   marker IMAGE W H DX
 *   loop PLAYLIST
 *   loop-return-mark INDEX  (jump back to mark 0 before the loop playlist finishes)
 *   playback menu|first CHAIN REWIND FIRST_PLAYLIST COUNT   (CHAIN, REWIND: 1 or 0; absent = menu, no chaining, back to the menu)
 *   defaults AUDIO SUB   (the audio / subtitle stream number the disc starts with; absent = 1 and 0, no subtitles)
 *   forced AUDIO SUB     (repeatable: with subtitles off and that audio stream playing, subtitle stream SUB is shown, a forced track the menu does not list)
 * Titles (BluRay.TitleLayout): the Top Menu and the last title run this xlet as the menu; title N (1 to the last but one) runs it to play playlist N, so a
 * video never plays inside the Top Menu title, where a player refuses the Top Menu key. What crosses a change of title goes through GPRs: 108 the menu button
 * to reopen on, 109 "the disc has just started", 110 the audio stream, 111 the subtitle stream + 1, 112 the chapter mark + 1, 113 the end of a run of videos
 * (exclusive), 114 the menu title (written by First Playback; 0 on a disc without that layout, where everything plays in the menu's title as before).
 * ACTION is "play PLAYLIST", "chapter PLAYLIST MARK", "page PAGE", "playall FIRST_PLAYLIST COUNT" (plays them one
 * after the other), "audio N" (stream number, 1-based) or "sub N" (0 = no subtitles). The first page is shown
 * first and the first button of a page has the initial focus. The marker image is drawn DX pixels from the left
 * edge of the "audio"/"sub" button that matches the current choice. Audio and subtitle choices are applied to the
 * player each time a playlist starts.
 * Pop-up menu: pages called "pop" + playlist number. While that playlist plays, the Pop-up key shows its page over the film, which keeps playing;
 * the key again or a "resume" button hides it. Actions there: "popup" (back to that page), "menu" (stop the title, main menu), and the usual
 * "chapter", "page", "audio" and "sub" (a track choice applies at once). A disc without such pages ignores the key, as before.
 *   popupkey CODE   (the key that opens and closes the pop-up instead of Pop-up, 461: BurnSmith never writes it; the VLC test does, VLC 3 has no Pop-up key)
 * With "loop", a short playlist plays behind the menu and starts again when it ends; the page images are then transparent overlays
 * (the graphics plane is cleared before each paint, or moved focus frames would stay on screen).
 */
public class Menu implements Xlet, UserEventListener, ControllerListener, PlaybackListener {

    private static class Button {
        String id, left, right, up, down, action, arg;
        int x, y, w, h, n1, n2;
        String focusName, activeName;
        Image focus, active;
        int motionX, motionY, motionW, motionH;
    }

    private static class Page {
        String id, bgName;
        Image bg;
        int markerDx = Integer.MIN_VALUE; // MIN_VALUE: the marker line's offset
        int loopPlaylist = -1; // -1: default global loopPlaylist
        Vector buttons = new Vector();
    }

    private class View extends Container {
        public void paint(Graphics g) {
            Page p = page;
            Button b = focus;
            if (activeLoopPlaylist > 0 || loopPlaylist > 0 || popupOpen) clear(g);
            if (p == null) return;
            Image bg = p.bg, hl = b == null ? null : (b == activeBtn && System.currentTimeMillis() < activeUntil && b.active != null ? b.active : b.focus); // read once: show() frees the previous page's pictures while this may run
            if (bg != null) g.drawImage(bg, 0, 0, this);
            Image[] frames = focusFrames;
            if (b != null && b.motionW > 0 && frames != null) {
                int frame = (int) ((System.currentTimeMillis() / 100) % frames.length);
                g.drawImage(frames[frame], b.motionX, b.motionY, b.motionW, b.motionH, this);
            } else if (hl != null) g.drawImage(hl, b.x, b.y, this);
            for (int i = 0; marker != null && i < p.buttons.size(); i++) {
                Button m = (Button) p.buttons.elementAt(i);
                boolean on = (m.action.equals("audio") && m.n1 == audio)
                    || (m.action.equals("sub") && m.n1 == (popupOpen ? subtitleChoice() : sub));
                if (on) g.drawImage(marker, m.x + (p.markerDx != Integer.MIN_VALUE ? p.markerDx : markerDx), m.y + (m.h - markerH) / 2, this);
            }
            long remaining = fadeEnd - System.currentTimeMillis();
            if (!popupOpen && remaining > 0 && pageFadeMs > 0 && g instanceof DVBGraphics) {
                DVBGraphics layer = (DVBGraphics) g;
                try {
                    layer.setDVBComposite(DVBAlphaComposite.getInstance(DVBAlphaComposite.SRC_OVER, Math.min(1.0f, (float) remaining / pageFadeMs)));
                    g.setColor(Color.black);
                    g.fillRect(0, 0, width, height);
                } catch (Throwable ignored) {
                } finally {
                    try { layer.setDVBComposite(DVBAlphaComposite.SrcOver); } catch (Throwable ignored) { }
                }
            }
        }
    }

    private XletContext ctx;
    private HScene scene;
    private View view;
    private volatile Player player;
    private volatile Player loopPlayer;
    private volatile PlaybackControl loopControl;
    private int pendingMark = -1; // chapter to skip to once the player starts (-1: none)
    private int loopPlaylist, activeLoopPlaylist; // menu video playing behind the pages, 0 = none
    private int loopReturnMark = -1;
    private volatile long nextLoopJump;
    private HSound moveSound, selectSound;
    private final Hashtable pages = new Hashtable();
    private Button activeBtn;
    private long activeUntil;
    private Page firstPage, page;
    /** Button of the first page the viewer last started something from, so the menu opens on it again (kept in GPR108). */
    private int lastButton = -1;
    private volatile Button focus;
    private final Vector focusFrameNames = new Vector();
    private volatile Image[] focusFrames;
    private Timer focusTimer;
    private boolean focusActive;
    private int width = 1920, height = 1080;
    private boolean listening;
    private int audio = 1, sub = 0; // current choice: audio stream number, subtitle stream number (0 = off)
    private int localSub = -1; // a pop-up choice that only exists on the current title
    /** "forced" lines: audio stream number -> the forced subtitle stream shown while subtitles are off (the lines the film's language needs). */
    private final Hashtable forced = new Hashtable();
    private final Hashtable subtitleTracks = new Hashtable(), forcedTracks = new Hashtable();
    private final Hashtable titleEnds = new Hashtable();
    private Image marker;
    private String markerName;
    private int markerW, markerH, markerDx;
    private int nextPlaylist, endPlaylist; // "play all": playlists still to come, [next, end)
    // "playback" line: where the disc starts and what happens when a title ends (see finished())
    private boolean startFirst, chain, rewind = true; // start on the first title / titles follow each other / back to the start afterwards
    private int autoStartSeconds;
    private Timer autoStartTimer;
    private int pageFadeMs;
    private volatile long fadeEnd;
    private String resumeKey;
    private ResumeStore resumeStore;
    private Timer resumeTimer;
    private int resumeAt;
    private long lastResume;
    private int titleFirst, titleCount; // the titles of the disc: playlists [first, first + count)
    private boolean hasPopup, popupOpen; // the disc has "pop" pages / one of them is shown over the film
    private int playing; // playlist of the running player
    private int popupKey = org.bluray.ui.event.HRcEvent.VK_POPUP_MENU;
    private static final int GPR_AUDIO = 110, GPR_SUB = 111, GPR_MARK = 112, GPR_END = 113, GPR_MENU_TITLE = 114, GPR_RESUME = 115;
    private int menuTitle; // the title that holds the menu again (GPR114), 0: no title per video, everything plays here
    private int videoTitle; // the title this xlet plays (title N = playlist N), 0 in the menu

    public void initXlet(XletContext c) throws XletStateChangeException {
        ctx = c;
        try {
            parse();
        } catch (Exception e) {
            throw new XletStateChangeException(e.toString());
        }
    }

    public void startXlet() throws XletStateChangeException {
        setFocusActive(true);
        // decoding the images can take a while: keep the xlet thread free
        new Thread() {
            public void run() {
                try {
                    open();
                } catch (Throwable t) {
                    t.printStackTrace();
                }
            }
        }.start();
    }

    public void pauseXlet() {
        stopCheckpoint(true);
        cancelAutoStart();
        setFocusActive(false);
    }

    public void destroyXlet(boolean unconditional) throws XletStateChangeException {
        stopCheckpoint(true);
        cancelAutoStart();
        setFocusActive(false);
        stopListening();
        stopLoop();
        closePlayer();
        if (scene != null) {
            scene.setVisible(false);
            HSceneFactory.getInstance().dispose(scene);
        }
    }

    private void open() throws Exception {
        scene = HSceneFactory.getInstance().getDefaultHScene();
        view = new View();
        view.setBounds(0, 0, width, height);
        scene.add(view);
        MediaTracker tracker = new MediaTracker(view);
        marker = load(markerName, tracker, 0);
        tracker.waitForAll();
        if (focusFrameNames.size() > 1 && focusFrames == null) {
            try {
                Image[] frames = new Image[focusFrameNames.size()];
                for (int i = 0; i < frames.length; i++)
                    frames[i] = load((String) focusFrameNames.elementAt(i), tracker, i + 1);
                tracker.waitForAll();
                focusFrames = frames;
                updateFocusTimer();
            } catch (Exception e) {
                log("animated focus unavailable: " + e);
            }
        }
        // the pages are decoded one at a time, when shown (see pictures): all of them at once (~8 MB each at 1920x1080) filled the player's
        // image memory, and the pages decoded last stayed empty on screen
        lastButton = readGpr(108) - 1; // the register holds index + 1, so 0 means "nothing remembered"
        if (readGpr(GPR_AUDIO) > 0) audio = readGpr(GPR_AUDIO); // the choice made in another title
        if (readGpr(GPR_SUB) > 0) sub = readGpr(GPR_SUB) - 1;
        menuTitle = readGpr(GPR_MENU_TITLE);
        if (resumeKey != null) {
            try { resumeStore = new ResumeStore(resumeKey); }
            catch (Exception e) { log("resume unavailable: " + e); }
        }
        int title = psr(4);
        if (menuTitle > 1 && title >= 1 && title < menuTitle) {
            videoTitle = title;
            int mark = readGpr(GPR_MARK) - 1, end = readGpr(GPR_END);
            resumeAt = Math.max(0, readGpr(GPR_RESUME));
            writeGpr(GPR_RESUME, 0);
            writeGpr(GPR_MARK, 0); // a later jump to this title by its number starts it from the beginning, alone
            writeGpr(GPR_END, 0);
            log("title " + title + ": video");
            nextPlaylist = end > title + 1 ? title + 1 : 0;
            endPlaylist = nextPlaylist > 0 ? end : 0;
            play(title, mark);
        } else {
            boolean initial = (startFirst || autoStartSeconds > 0 || resumeStore != null) && titleCount > 0 && firstPlayback();
            if (initial && restoreCheckpoint()) return;
            if (initial && startFirst) startSequence();
            else {
                showMenu();
                if (initial) scheduleAutoStart();
            }
        }
    }

    private boolean restoreCheckpoint() {
        if (resumeStore == null || menuTitle <= 1) return false;
        try {
            long mark = resumeStore.read();
            int title = (int) (mark >>> 32), position = (int) mark;
            if (title < 1 || title >= menuTitle || position < 1000) return false;
            writeGpr(GPR_MARK, 0);
            writeGpr(GPR_END, follow(title));
            writeGpr(GPR_RESUME, position);
            if (selectTitle(title)) return true;
            writeGpr(GPR_RESUME, 0);
        } catch (Exception e) { log("resume read unavailable: " + e); }
        return false;
    }

    private synchronized void startCheckpoint() {
        if (resumeStore == null || resumeTimer != null) return;
        final Timer timer = new Timer(true);
        resumeTimer = timer;
        timer.schedule(new TimerTask() {
            public void run() {
                synchronized (Menu.this) {
                    if (resumeTimer == timer) checkpoint();
                }
            }
        }, 1000L, 2000L);
    }

    private synchronized void stopCheckpoint(boolean save) {
        if (resumeTimer == null) return;
        resumeTimer.cancel();
        resumeTimer = null;
        if (save) checkpoint();
    }

    private synchronized void checkpoint() {
        Player current = player;
        if (resumeStore == null || current == null || playing < 1 || playing >= menuTitle) return;
        try {
            double ms = current.getMediaTime().getSeconds() * 1000.0;
            if (ms < 1000 || ms > Integer.MAX_VALUE) return;
            long value = ((long) playing << 32) | (int) ms;
            if (value != lastResume) resumeStore.write(value);
            lastResume = value;
        } catch (Exception e) {
            log("resume write unavailable: " + e);
            stopCheckpoint(false);
            resumeStore = null;
        }
    }

    private synchronized void cancelAutoStart() {
        if (autoStartTimer == null) return;
        autoStartTimer.cancel();
        autoStartTimer = null;
    }

    private synchronized void scheduleAutoStart() {
        if (!focusActive) return;
        cancelAutoStart();
        autoStartTimer = new Timer(true);
        autoStartTimer.schedule(new TimerTask() {
            public void run() { autoStart(); }
        }, autoStartSeconds * 1000L);
    }

    private synchronized void autoStart() {
        if (autoStartTimer == null) return;
        cancelAutoStart();
        if (!focusActive || player != null) return;
        log("auto-start: " + autoStartSeconds + " s");
        start(titleFirst, -1, follow(titleFirst));
    }

    private synchronized void setFocusActive(boolean active) {
        focusActive = active;
        updateFocusTimer();
    }

    private synchronized void updateFocusTimer() {
        if (!focusActive && focusTimer != null) {
            focusTimer.cancel();
            focusTimer = null;
        }
        if (!focusActive || (focusFrames == null && pageFadeMs == 0) || focusTimer != null) return;
        focusTimer = new Timer(true);
        focusTimer.schedule(new TimerTask() {
            public void run() {
                Button b = focus;
                long end = fadeEnd;
                if (end > 0 && System.currentTimeMillis() >= end) fadeEnd = 0;
                if ((end > 0 || focusFrames != null && b != null && b.motionW > 0) && scene.isVisible()) view.repaint();
            }
        }, 0, pageFadeMs > 0 ? 33 : 100);
    }

    private int psr(int n) {
        try {
            return RegisterAccess.getInstance().getPSR(n);
        } catch (Throwable t) {
            return -1;
        }
    }

    /** Selects title <code>title</code>: this xlet stops, the title's own starts. False when the player refuses; the caller then plays here. */
    private boolean selectTitle(int title) {
        try {
            log("select title " + title);
            ServiceContextFactory.getInstance().getServiceContext(ctx).select(SIManager.createInstance().getService(new BDLocator(null, title, -1)));
            return true;
        } catch (Throwable t) {
            log("title " + title + " not selected: " + t);
            return false;
        }
    }

    /**
     * Plays <code>playlist</code> from chapter <code>mark</code> (-1: the start), followed by the next ones up to <code>end</code> (exclusive, 0: alone).
     * Each video plays in its own title (the next of a run too); a chapter picked in the pop-up plays in the title already running.
     */
    private void start(int playlist, int mark, int end) {
        if (playlist != videoTitle && menuTitle > 1 && playlist >= 1 && playlist < menuTitle) {
            writeGpr(GPR_MARK, mark + 1);
            writeGpr(GPR_END, end);
            if (loopReturnMark >= 0) stopLoop(); // retire the old player's seek requests before selecting the film title
            if (selectTitle(playlist)) return;
        }
        nextPlaylist = end > playlist + 1 ? playlist + 1 : 0;
        endPlaylist = nextPlaylist > 0 ? end : 0;
        play(playlist, mark);
    }

    /** Back to the menu: its title when the video plays in its own, else on this page. */
    private void toMenu() {
        if (videoTitle > 0 && selectTitle(menuTitle)) return;
        showMenu();
    }

    /**
     * First Playback is an HDMV object that sets GPR109 to 1 before it jumps to the menu title (BluRay.TitleLayout), so the disc only starts on the
     * first title the first time. The register is cleared at once: the Top Menu key, or a video that ends, shows the menu.
     */
    private boolean firstPlayback() {
        int start = readGpr(109);
        if (start < 0) return true; // no register access: as if the disc had just started
        writeGpr(109, 0);
        return start == 1;
    }

    /** "Start from the first title": no menu, the titles play one after the other. */
    private void startSequence() {
        start(titleFirst, -1, titleFirst + titleCount);
    }

    /**
     * Nothing left to play. A playback error, "back to the menu" mode and "rewind" return to the start (the menu, or the first title
     * when the disc starts there); otherwise the disc stops: the player's own Menu key starts it again.
     */
    private void finished(boolean normalEnd) {
        nextPlaylist = endPlaylist = 0;
        if (normalEnd && chain && titleCount > 0 && startFirst && rewind) {
            log("rewind: first title again");
            startSequence();
        } else if (!normalEnd || !chain || rewind) {
            toMenu();
        } else {
            log("playback finished: stopped");
        }
    }

    private void showMenu() {
        popupOpen = false;
        localSub = -1;
        startLoop();
        show(firstPage);
        scene.setVisible(true);
        scene.requestFocus(); // keys only reach the xlet that owns the focus
        startListening();
    }

    /** Shows up in libbluray's log; players that do not log stdout ignore it. */
    private static void log(String s) {
        System.out.println("burnsmith: " + s);
    }

    private void show(Page p) {
        log("page " + p.id);
        Page old = page;
        pictures(p, true);
        page = p;
        if (!popupOpen && player == null) {
            int wantLoop = p.loopPlaylist >= 0 ? p.loopPlaylist : loopPlaylist;
            if (wantLoop != activeLoopPlaylist) {
                stopLoop();
                startLoop(wantLoop);
            }
        }
        fadeEnd = !popupOpen && old != null && old != p && pageFadeMs > 0 ? System.currentTimeMillis() + pageFadeMs : 0;
        focus = p.buttons.isEmpty() ? null : (Button) p.buttons.elementAt(0);
        // back from a title: the button that started it is focused again, from GPR108
        if (p == firstPage && lastButton >= 0 && lastButton < p.buttons.size()) {
            focus = (Button) p.buttons.elementAt(lastButton);
            log("resume on button " + lastButton);
        }
        if (old != null && old != p) pictures(old, false);
        view.repaint();
    }

    /** A GPR the player may refuse: -1 then, and the menu simply starts on its first button. */
    private int readGpr(int n) {
        try {
            return RegisterAccess.getInstance().getGPR(n);
        } catch (Throwable t) {
            return -1;
        }
    }

    private void writeGpr(int n, int value) {
        try {
            RegisterAccess.getInstance().setGPR(n, value);
        } catch (Throwable t) {
            // no register access: the menu still works, it just forgets where the viewer was
        }
    }

    private void startListening() {
        if (listening) return;
        UserEventRepository keys = new UserEventRepository("burnsmith");
        keys.addKey(HRcEvent.VK_LEFT);
        keys.addKey(HRcEvent.VK_RIGHT);
        keys.addKey(HRcEvent.VK_UP);
        keys.addKey(HRcEvent.VK_DOWN);
        keys.addKey(HRcEvent.VK_ENTER);
        if (hasPopup) keys.addKey(popupKey);
        EventManager.getInstance().addUserEventListener(this, keys);
        listening = true;
    }

    private void stopListening() {
        if (!listening) return;
        EventManager.getInstance().removeUserEventListener(this);
        listening = false;
    }

    public void userEventReceived(UserEvent e) {
        if (e.getType() != KeyEvent.KEY_PRESSED) return;
        cancelAutoStart();
        int code = e.getCode();
        if (code == popupKey) {
            if (popupOpen) closePopup();
            else if (player != null) openPopup("pop" + playing);
            return;
        }
        if (player != null && !popupOpen) return; // the film plays: the hidden menu page takes no key
        if (focus == null) return;
        switch (code) {
            case HRcEvent.VK_LEFT:  move(focus.left);  break;
            case HRcEvent.VK_RIGHT: move(focus.right); break;
            case HRcEvent.VK_UP:    move(focus.up);    break;
            case HRcEvent.VK_DOWN:  move(focus.down);  break;
            case HRcEvent.VK_ENTER: run(focus);        break;
        }
    }

    private void playSound(HSound s) {
        if (s == null) return;
        try { s.play(); } catch (Throwable ignored) { }
    }

    private void move(String id) {
        if (id == null) return;
        for (int i = 0; i < page.buttons.size(); i++) {
            Button b = (Button) page.buttons.elementAt(i);
            if (b.id.equals(id)) {
                focus = b;
                playSound(moveSound);
                view.repaint();
                return;
            }
        }
    }

    private void run(Button b) {
        playSound(selectSound);
        if (b.active != null) {
            activeBtn = b;
            activeUntil = System.currentTimeMillis() + 160;
            view.repaint();
        }
        if (b.action.equals("page")) {
            Page p = (Page) pages.get(b.arg);
            if (p != null) show(p);
        } else if (b.action.equals("play")) {
            remember(b);
            start(b.n1, -1, follow(b.n1));
        } else if (b.action.equals("chapter")) {
            remember(b);
            start(b.n1, b.n2, follow(b.n1));
        } else if (b.action.equals("playall")) {
            remember(b);
            start(b.n1, -1, b.n1 + b.n2);
        } else if (b.action.equals("audio")) {
            audio = b.n1;
            writeGpr(GPR_AUDIO, audio);
            log("audio choice " + audio);
            if (player != null) applyTracks(player); // chosen in the pop-up: the film changes at once
            view.repaint();
        } else if (b.action.equals("sub")) {
            if (popupOpen && b.n1 > 0 && !hasSubtitle(titleFirst, b.n1)) localSub = b.n1;
            else {
                localSub = -1;
                sub = b.n1;
                writeGpr(GPR_SUB, sub + 1);
            }
            log("subtitle choice " + b.n1);
            if (player != null) applyTracks(player);
            view.repaint();
        } else if (b.action.equals("resume")) {
            closePopup();
        } else if (b.action.equals("popup")) {
            openPopup("pop" + playing);
        } else if (b.action.equals("menu")) {
            log("pop-up: main menu");
            nextPlaylist = endPlaylist = 0;
            stopCheckpoint(true);
            closePlayer();
            toMenu();
        }
    }

    private void openPopup(String id) {
        Page p = (Page) pages.get(id);
        if (p == null) return;
        log("pop-up open");
        popupOpen = true;
        show(p);
        scene.setVisible(true);
        scene.requestFocus();
    }

    private void closePopup() {
        log("pop-up closed");
        popupOpen = false;
        scene.setVisible(false);
        view.repaint();
    }

    /** Remembers where the viewer started from, so the menu reopens on that button (GPR108). */
    private void remember(Button b) {
        if (page != firstPage) return; // the index only means something on the page the menu reopens on
        int i = page.buttons.indexOf(b);
        if (i < 0) return;
        lastButton = i;
        writeGpr(108, i + 1);
    }

    /** Where a run that starts at the title picked in the menu ends (exclusive): the disc's last title when it plays everything in a row, else 0 (alone). */
    private int follow(int playlist) {
        return chain && titleCount > 0 && playlist >= titleFirst && playlist < titleFirst + titleCount ? titleFirst + titleCount : 0;
    }

    private boolean hasSubtitle(int playlist, int number) {
        Vector tracks = (Vector) subtitleTracks.get(new Integer(playlist));
        return tracks == null || tracks.contains(new Integer(number)); // older menus have no playlist track table
    }

    private int subtitleChoice() {
        return localSub >= 0 ? localSub : hasSubtitle(playing, sub) ? sub : 0;
    }

    /** Applies the audio / subtitle choice to a playlist that is running. Failures only cost the choice, never the playback. */
    private void applyTracks(Player p) {
        int played = audio;
        try {
            Control c = p.getControl("org.bluray.media.PrimaryAudioControl");
            if (c instanceof PrimaryAudioControl) {
                // a bonus can hold fewer audio streams than the titles the choice was made for: a number it has not would play it silent
                int[] have = ((PrimaryAudioControl) c).listAvailableStreamNumbers();
                if (have != null && have.length > 0) {
                    played = have[0];
                    for (int i = 0; i < have.length; i++) if (have[i] == audio) played = audio;
                }
                ((PrimaryAudioControl) c).selectStreamNumber(played);
                log("audio stream " + played + " selected");
            } else {
                log("no PrimaryAudioControl");
            }
        } catch (Throwable t) {
            log("audio selection failed: " + t);
        }
        try {
            Control c = p.getControl("org.bluray.media.SubtitlingControl");
            if (c instanceof SubtitlingControl) {
                SubtitlingControl s = (SubtitlingControl) c;
                int selected = subtitleChoice();
                Integer own = selected > 0 ? null : (Integer) (subtitleTracks.containsKey(new Integer(playing))
                    ? forcedTracks.get(playing + ":" + played) : forced.get(new Integer(played)));
                int shown = selected > 0 ? selected : own != null ? own.intValue() : 0;
                if (shown > 0) {
                    s.selectStreamNumber(shown);
                    s.setSubtitling(true);
                } else {
                    s.setSubtitling(false);
                }
                log("subtitle stream " + shown + " selected" + (own != null ? " (forced, audio " + played + ")" : ""));
            } else {
                log("no SubtitlingControl");
            }
        } catch (Throwable t) {
            log("subtitle selection failed: " + t);
        }
    }

    private void play(int playlist, int mark) {
        log("play playlist " + playlist + " mark " + mark);
        try {
            // the playlist alone, then a skip to the chapter once it plays: a locator with ITEM:00 and MARK together played in VLC
            // but sent a real player back to the menu on every chapter (Bright, 2026-11-13)
            BDLocator loc = new BDLocator(null, -1, playlist);
            closePlayer();
            pendingMark = mark; // a chapter picked in the pop-up replaces the title that plays
            popupOpen = false;
            scene.setVisible(false);
            if (hasPopup) startListening(); // the Pop-up key must reach the xlet while the film plays
            else stopListening();
            stopLoop();
            if (playing != playlist) localSub = -1;
            playing = playlist;
            player = Manager.createPlayer(new MediaLocator(loc.toExternalForm()));
            player.addControllerListener(this);
            player.start();
        } catch (Exception ex) {
            ex.printStackTrace();
            closePlayer();
            toMenu();
        }
    }

    /** Starts the menu video, if the disc has one. A failure only costs the video: the menu still works over black. */
    private void startLoop() {
        int want = page != null && page.loopPlaylist >= 0 ? page.loopPlaylist : loopPlaylist;
        startLoop(want);
    }

    private void startLoop(int targetPlaylist) {
        if (targetPlaylist <= 0 || loopPlayer != null) return;
        try {
            activeLoopPlaylist = targetPlaylist;
            BDLocator loc = new BDLocator(null, -1, targetPlaylist);
            nextLoopJump = 0;
            loopPlayer = Manager.createPlayer(new MediaLocator(loc.toExternalForm()));
            loopPlayer.addControllerListener(this);
            loopPlayer.start();
            log("loop started playlist " + targetPlaylist);
        } catch (Throwable t) {
            log("loop failed: " + t);
            stopLoop();
        }
    }

    private void stopLoop() {
        Player p = loopPlayer;
        loopPlayer = null;
        activeLoopPlaylist = 0;
        PlaybackControl control = loopControl;
        loopControl = null;
        if (control != null) {
            try { control.removePlaybackControlListener(this); }
            catch (Throwable ignored) { }
        }
        if (p == null) return;
        try {
            p.removeControllerListener(this);
            p.stop();
            p.close();
        } catch (Throwable ignored) {
        }
    }

    /** Transparent again: pages are overlays on the menu video. */
    private void clear(Graphics g) {
        try {
            DVBGraphics d = (DVBGraphics) g;
            d.setDVBComposite(DVBAlphaComposite.Clear);
            g.fillRect(0, 0, width, height);
            d.setDVBComposite(DVBAlphaComposite.SrcOver);
        } catch (Throwable t) {
            g.clearRect(0, 0, width, height);
        }
    }

    /** End of the playlist (or a playback error): back to the menu. */
    public void controllerUpdate(ControllerEvent e) {
        Controller from = e.getSourceController();
        if (from != null && from == loopPlayer) {
            if (e instanceof StartEvent && loopReturnMark >= 0 && loopControl == null) {
                try {
                    Control control = from.getControl("org.bluray.media.PlaybackControl");
                    if (control instanceof PlaybackControl && from == loopPlayer) {
                        loopControl = (PlaybackControl) control;
                        loopControl.addPlaybackControlListener(this);
                    }
                } catch (Throwable t) {
                    log("menu loop mark unavailable: " + t);
                }
            } else if (e instanceof EndOfMediaEvent) {
                log("loop restarts");
                int cur = activeLoopPlaylist;
                stopLoop();
                startLoop(cur > 0 ? cur : loopPlaylist);
            } else if (e instanceof ControllerErrorEvent) {
                log("loop error: " + e.getClass().getName());
                stopLoop();
            }
            return;
        }
        if (from == null || from != player) return;
        if (e instanceof StartEvent) {
            applyTracks(player);
            skipToChapter(player);
            if (resumeAt > 0) {
                int position = resumeAt;
                resumeAt = 0;
                try {
                    if (position / 1000.0 < player.getDuration().getSeconds()) {
                        player.setMediaTime(new Time(position * 1000000L));
                        log("resume title " + playing + " at " + position + " ms");
                    }
                } catch (Exception ex) { log("resume seek unavailable: " + ex); }
            }
            startCheckpoint();
        } else if (e instanceof EndOfMediaEvent || e instanceof ControllerErrorEvent) {
            stopCheckpoint(false);
            if (e instanceof EndOfMediaEvent && resumeStore != null) {
                try { resumeStore.write(0); lastResume = 0; }
                catch (Exception ex) { log("resume clear unavailable: " + ex); }
            }
            log("playback ended: " + e.getClass().getName());
            closePlayer();
            String action = e instanceof EndOfMediaEvent ? (String) titleEnds.get(new Integer(playing)) : null;
            if (action != null) {
                log("title end " + playing + ": " + action);
                if (action.equals("menu")) {
                    nextPlaylist = endPlaylist = 0;
                    toMenu();
                } else if (action.equals("repeat")) start(playing, -1, 0);
                else if (action.equals("next") && playing >= titleFirst && playing + 1 < titleFirst + titleCount)
                    start(playing + 1, -1, endPlaylist);
                else if (action.equals("next")) toMenu();
                else {
                    nextPlaylist = endPlaylist = 0;
                    if (popupOpen) closePopup();
                }
            } else if (e instanceof EndOfMediaEvent && nextPlaylist > 0 && nextPlaylist < endPlaylist) start(nextPlaylist, -1, endPlaylist);
            else finished(e instanceof EndOfMediaEvent);
        }
    }

    public void markReached(PlaybackMarkEvent event) {
        PlaybackControl control = loopControl;
        Player loop = loopPlayer;
        if (loop == null || control == null || event.getSource() != control || event.getMark() != loopReturnMark) return;
        long now = System.currentTimeMillis();
        if (now < nextLoopJump) return;
        try {
            // Read-ahead can report the same mark again as soon as the seek refills the buffers.
            double seconds = loop.getDuration().getSeconds();
            nextLoopJump = now + (seconds > 0 && seconds <= 1801 ? (long) (seconds * 400) : 1000);
            control.skipToMark(0);
            log("menu loop jumped to mark 0");
        } catch (Throwable t) {
            log("menu loop jump failed: " + t);
        }
    }

    public void playItemReached(PlaybackPlayItemEvent event) { }

    /** The chapter asked for, once the playlist plays. A refusal only costs the chapter: the film plays from its start. */
    private void skipToChapter(Player p) {
        int mark = pendingMark;
        pendingMark = -1;
        if (mark < 0 || p == null) return;
        try {
            Control c = p.getControl("org.bluray.media.PlaybackControl");
            if (c instanceof PlaybackControl) {
                ((PlaybackControl) c).skipToMark(mark);
                log("skip to mark " + mark);
            } else {
                log("no PlaybackControl");
            }
        } catch (Throwable t) {
            log("skip to mark failed: " + t);
        }
    }

    private void closePlayer() {
        Player p = player;
        player = null;
        if (p == null) return;
        try {
            p.removeControllerListener(this);
            p.stop();
            p.close();
        } catch (Exception ignored) {
        }
    }

    /** Decodes a page's background and focus pictures (on), or frees them (off): only the page on screen holds image memory. */
    private void pictures(Page p, boolean on) {
        if (!on) {
            if (p.bg != null) p.bg.flush();
            p.bg = null;
            for (int i = 0; i < p.buttons.size(); i++) {
                Button b = (Button) p.buttons.elementAt(i);
                if (b.focus != null) b.focus.flush();
                b.focus = null;
                if (b.active != null) b.active.flush();
                b.active = null;
            }
            return;
        }
        if (p.bg != null) return;
        try {
            MediaTracker tracker = new MediaTracker(view);
            p.bg = load(p.bgName, tracker, 0);
            for (int i = 0; i < p.buttons.size(); i++) {
                Button b = (Button) p.buttons.elementAt(i);
                b.focus = load(b.focusName, tracker, i + 1);
                if (b.activeName != null) b.active = load(b.activeName, tracker, p.buttons.size() + i + 1);
            }
            tracker.waitForAll(); // no isErrorAny() check: libbluray's tracker flags every picture as failed though they draw (seen in VLC)
        } catch (Throwable t) {
            log("page " + p.id + ": pictures not loaded: " + t);
        }
    }

    private Image load(String name, MediaTracker tracker, int id) throws Exception {
        if (name == null) return null;
        InputStream in = Menu.class.getResourceAsStream("/" + name);
        if (in == null) throw new Exception("missing resource " + name);
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        for (int n; (n = in.read(buf)) > 0;) out.write(buf, 0, n);
        in.close();
        Image img = Toolkit.getDefaultToolkit().createImage(out.toByteArray());
        tracker.addImage(img, id);
        return img;
    }

    private HSound loadSound(String name) {
        if (name == null) return null;
        try {
            InputStream in = Menu.class.getResourceAsStream("/" + name);
            if (in == null) return null;
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buf = new byte[4096];
            for (int n; (n = in.read(buf)) > 0;) out.write(buf, 0, n);
            in.close();
            HSound hs = new HSound();
            hs.set(out.toByteArray());
            return hs;
        } catch (Throwable t) {
            return null;
        }
    }

    private static String dash(String s) {
        return s.equals("-") ? null : s;
    }

    private void parse() throws Exception {
        InputStream in = Menu.class.getResourceAsStream("/menu.txt");
        BufferedReader r = new BufferedReader(new InputStreamReader(in, "UTF-8"));
        Button last = null;
        for (String line; (line = r.readLine()) != null;) {
            line = line.trim();
            if (line.length() == 0 || line.charAt(0) == '#') continue;
            StringTokenizer t = new StringTokenizer(line);
            String cmd = t.nextToken();
            if (cmd.equals("size")) {
                width = Integer.parseInt(t.nextToken());
                height = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("marker")) {
                markerName = dash(t.nextToken());
                markerW = Integer.parseInt(t.nextToken());
                markerH = Integer.parseInt(t.nextToken());
                markerDx = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("playback")) {
                startFirst = t.nextToken().equals("first");
                chain = t.nextToken().equals("1");
                rewind = t.nextToken().equals("1");
                titleFirst = Integer.parseInt(t.nextToken());
                titleCount = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("auto-start")) {
                autoStartSeconds = Math.max(0, Math.min(300, Integer.parseInt(t.nextToken())));
            } else if (cmd.equals("resume-key")) {
                resumeKey = t.nextToken();
            } else if (cmd.equals("page-fade")) {
                pageFadeMs = Math.max(0, Math.min(500, Integer.parseInt(t.nextToken())));
            } else if (cmd.equals("title-end")) {
                Integer playlist = new Integer(Integer.parseInt(t.nextToken()));
                titleEnds.put(playlist, t.nextToken());
            } else if (cmd.equals("defaults")) {
                audio = Integer.parseInt(t.nextToken());
                sub = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("forced")) {
                forced.put(new Integer(Integer.parseInt(t.nextToken())), new Integer(Integer.parseInt(t.nextToken())));
            } else if (cmd.equals("subtracks")) {
                Integer playlist = new Integer(Integer.parseInt(t.nextToken()));
                Vector tracks = new Vector();
                while (t.hasMoreTokens()) tracks.addElement(new Integer(Integer.parseInt(t.nextToken())));
                subtitleTracks.put(playlist, tracks);
            } else if (cmd.equals("forced-track")) {
                String key = t.nextToken() + ":" + t.nextToken();
                forcedTracks.put(key, new Integer(Integer.parseInt(t.nextToken())));
            } else if (cmd.equals("popupkey")) {
                popupKey = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("loop")) {
                loopPlaylist = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("loop-return-mark")) {
                loopReturnMark = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("page-loop")) {
                Page lp = (Page) pages.get(t.nextToken());
                if (lp != null) lp.loopPlaylist = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("sound-move")) {
                moveSound = loadSound(t.nextToken());
            } else if (cmd.equals("sound-select")) {
                selectSound = loadSound(t.nextToken());
            } else if (cmd.equals("focus-frames")) {
                while (t.hasMoreTokens()) focusFrameNames.addElement(t.nextToken());
            } else if (cmd.equals("button-act")) {
                Page p = (Page) pages.get(t.nextToken());
                String bid = t.nextToken();
                String aname = dash(t.nextToken());
                if (p != null) {
                    for (int i = 0; i < p.buttons.size(); i++) {
                        Button qb = (Button) p.buttons.elementAt(i);
                        if (qb.id.equals(bid)) { qb.activeName = aname; break; }
                    }
                }
            } else if (cmd.equals("focus-slot")) {
                if (last == null) throw new Exception("menu.txt: focus-slot without a button");
                last.motionX = Integer.parseInt(t.nextToken());
                last.motionY = Integer.parseInt(t.nextToken());
                last.motionW = Integer.parseInt(t.nextToken());
                last.motionH = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("page")) {
                Page p = new Page();
                p.id = t.nextToken();
                p.bgName = dash(t.nextToken());
                if (t.hasMoreTokens()) p.markerDx = Integer.parseInt(t.nextToken());
                if (p.id.startsWith("pop")) hasPopup = true;
                pages.put(p.id, p);
                if (firstPage == null) firstPage = p;
            } else if (cmd.equals("button")) {
                Page p = (Page) pages.get(t.nextToken());
                Button b = new Button();
                b.id = t.nextToken();
                b.x = Integer.parseInt(t.nextToken());
                b.y = Integer.parseInt(t.nextToken());
                b.w = Integer.parseInt(t.nextToken());
                b.h = Integer.parseInt(t.nextToken());
                b.focusName = dash(t.nextToken());
                b.left = dash(t.nextToken());
                b.right = dash(t.nextToken());
                b.up = dash(t.nextToken());
                b.down = dash(t.nextToken());
                b.action = t.nextToken();
                if (t.hasMoreTokens()) {
                    b.arg = t.nextToken();
                    try {
                        b.n1 = Integer.parseInt(b.arg);
                    } catch (NumberFormatException ignored) {
                    }
                }
                if (t.hasMoreTokens()) b.n2 = Integer.parseInt(t.nextToken());
                p.buttons.addElement(b);
                last = b;
            } else {
                throw new Exception("menu.txt: unknown directive " + cmd);
            }
        }
        r.close();
        if (firstPage == null) throw new Exception("menu.txt: no page");
    }
}
