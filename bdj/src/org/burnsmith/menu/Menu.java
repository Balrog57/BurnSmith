package org.burnsmith.menu;

import java.awt.Container;
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
import javax.tv.service.SIManager;
import javax.tv.service.selection.ServiceContextFactory;
import javax.tv.xlet.Xlet;
import javax.tv.xlet.XletContext;
import javax.tv.xlet.XletStateChangeException;

import org.bluray.media.PrimaryAudioControl;
import org.bluray.media.SubtitlingControl;
import org.bluray.net.BDLocator;
import org.bluray.system.RegisterAccess;
import org.dvb.event.EventManager;
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
 *   marker IMAGE W H DX
 *   loop PLAYLIST
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
public class Menu implements Xlet, UserEventListener, ControllerListener {

    private static class Button {
        String id, left, right, up, down, action, arg;
        int x, y, w, h, n1, n2;
        String focusName;
        Image focus;
    }

    private static class Page {
        String id, bgName;
        Image bg;
        int markerDx = Integer.MIN_VALUE; // MIN_VALUE: the marker line's offset
        Vector buttons = new Vector();
    }

    private class View extends Container {
        public void paint(Graphics g) {
            Page p = page;
            Button b = focus;
            if (loopPlaylist > 0 || popupOpen) clear(g);
            if (p == null) return;
            Image bg = p.bg, hl = b == null ? null : b.focus; // read once: show() frees the previous page's pictures while this may run
            if (bg != null) g.drawImage(bg, 0, 0, this);
            if (hl != null) g.drawImage(hl, b.x, b.y, this);
            if (marker == null) return;
            for (int i = 0; i < p.buttons.size(); i++) {
                Button m = (Button) p.buttons.elementAt(i);
                boolean on = (m.action.equals("audio") && m.n1 == audio) || (m.action.equals("sub") && m.n1 == sub);
                if (on) g.drawImage(marker, m.x + (p.markerDx != Integer.MIN_VALUE ? p.markerDx : markerDx), m.y + (m.h - markerH) / 2, this);
            }
        }
    }

    private XletContext ctx;
    private HScene scene;
    private View view;
    private Player player, loopPlayer;
    private int loopPlaylist; // menu video playing behind the pages, 0 = none
    private final Hashtable pages = new Hashtable();
    private Page firstPage, page;
    /** Button of the first page the viewer last started something from, so the menu opens on it again (kept in GPR108). */
    private int lastButton = -1;
    private Button focus;
    private int width = 1920, height = 1080;
    private boolean listening;
    private int audio = 1, sub = 0; // current choice: audio stream number, subtitle stream number (0 = off)
    /** "forced" lines: audio stream number -> the forced subtitle stream shown while subtitles are off (the lines the film's language needs). */
    private final Hashtable forced = new Hashtable();
    private Image marker;
    private String markerName;
    private int markerW, markerH, markerDx;
    private int nextPlaylist, endPlaylist; // "play all": playlists still to come, [next, end)
    // "playback" line: where the disc starts and what happens when a title ends (see finished())
    private boolean startFirst, chain, rewind = true; // start on the first title / titles follow each other / back to the start afterwards
    private int titleFirst, titleCount; // the titles of the disc: playlists [first, first + count)
    private boolean hasPopup, popupOpen; // the disc has "pop" pages / one of them is shown over the film
    private int playing; // playlist of the running player
    private int popupKey = org.bluray.ui.event.HRcEvent.VK_POPUP_MENU;
    private static final int GPR_AUDIO = 110, GPR_SUB = 111, GPR_MARK = 112, GPR_END = 113, GPR_MENU_TITLE = 114;
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
    }

    public void destroyXlet(boolean unconditional) throws XletStateChangeException {
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
        // the pages are decoded one at a time, when shown (see pictures): all of them at once (~8 MB each at 1920x1080) filled the player's
        // image memory, and the pages decoded last stayed empty on screen
        lastButton = readGpr(108) - 1; // the register holds index + 1, so 0 means "nothing remembered"
        if (readGpr(GPR_AUDIO) > 0) audio = readGpr(GPR_AUDIO); // the choice made in another title
        if (readGpr(GPR_SUB) > 0) sub = readGpr(GPR_SUB) - 1;
        menuTitle = readGpr(GPR_MENU_TITLE);
        int title = psr(4);
        if (menuTitle > 1 && title >= 1 && title < menuTitle) {
            videoTitle = title;
            int mark = readGpr(GPR_MARK) - 1, end = readGpr(GPR_END);
            writeGpr(GPR_MARK, 0); // a later jump to this title by its number starts it from the beginning, alone
            writeGpr(GPR_END, 0);
            log("title " + title + ": video");
            nextPlaylist = end > title + 1 ? title + 1 : 0;
            endPlaylist = nextPlaylist > 0 ? end : 0;
            play(title, mark);
        } else if (startFirst && titleCount > 0 && firstPlayback()) startSequence();
        else showMenu();
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

    private void move(String id) {
        if (id == null) return;
        for (int i = 0; i < page.buttons.size(); i++) {
            Button b = (Button) page.buttons.elementAt(i);
            if (b.id.equals(id)) {
                focus = b;
                view.repaint();
                return;
            }
        }
    }

    private void run(Button b) {
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
            sub = b.n1;
            writeGpr(GPR_SUB, sub + 1);
            log("subtitle choice " + sub);
            if (player != null) applyTracks(player);
            view.repaint();
        } else if (b.action.equals("resume")) {
            closePopup();
        } else if (b.action.equals("popup")) {
            openPopup("pop" + playing);
        } else if (b.action.equals("menu")) {
            log("pop-up: main menu");
            nextPlaylist = endPlaylist = 0;
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

    /** Applies the audio / subtitle choice to a playlist that is running. Failures only cost the choice, never the playback. */
    private void applyTracks(Player p) {
        try {
            Control c = p.getControl("org.bluray.media.PrimaryAudioControl");
            if (c instanceof PrimaryAudioControl) {
                // a bonus can hold fewer audio streams than the titles the choice was made for: a number it has not would play it silent
                int[] have = ((PrimaryAudioControl) c).listAvailableStreamNumbers();
                int played = audio;
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
                Integer own = sub > 0 ? null : (Integer) forced.get(new Integer(audio));
                int shown = sub > 0 ? sub : own != null ? own.intValue() : 0;
                if (shown > 0) {
                    s.selectStreamNumber(shown);
                    s.setSubtitling(true);
                } else {
                    s.setSubtitling(false);
                }
                log("subtitle stream " + shown + " selected" + (own != null ? " (forced, audio " + audio + ")" : ""));
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
            BDLocator loc = mark < 0
                ? new BDLocator(null, -1, playlist)
                : new BDLocator(null, -1, playlist, 0, mark, null);
            closePlayer(); // a chapter picked in the pop-up replaces the title that plays
            popupOpen = false;
            scene.setVisible(false);
            if (hasPopup) startListening(); // the Pop-up key must reach the xlet while the film plays
            else stopListening();
            stopLoop();
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
        if (loopPlaylist <= 0 || loopPlayer != null) return;
        try {
            BDLocator loc = new BDLocator(null, -1, loopPlaylist);
            loopPlayer = Manager.createPlayer(new MediaLocator(loc.toExternalForm()));
            loopPlayer.addControllerListener(this);
            loopPlayer.start();
            log("loop started playlist " + loopPlaylist);
        } catch (Throwable t) {
            log("loop failed: " + t);
            stopLoop();
        }
    }

    private void stopLoop() {
        Player p = loopPlayer;
        loopPlayer = null;
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
            if (e instanceof EndOfMediaEvent) {
                log("loop restarts");
                stopLoop();
                startLoop();
            } else if (e instanceof ControllerErrorEvent) {
                log("loop error: " + e.getClass().getName());
                stopLoop();
            }
            return;
        }
        if (e instanceof StartEvent) {
            applyTracks(player);
        } else if (e instanceof EndOfMediaEvent || e instanceof ControllerErrorEvent) {
            log("playback ended: " + e.getClass().getName());
            closePlayer();
            if (e instanceof EndOfMediaEvent && nextPlaylist > 0 && nextPlaylist < endPlaylist) start(nextPlaylist, -1, endPlaylist);
            else finished(e instanceof EndOfMediaEvent);
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

    private static String dash(String s) {
        return s.equals("-") ? null : s;
    }

    private void parse() throws Exception {
        InputStream in = Menu.class.getResourceAsStream("/menu.txt");
        BufferedReader r = new BufferedReader(new InputStreamReader(in, "UTF-8"));
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
            } else if (cmd.equals("defaults")) {
                audio = Integer.parseInt(t.nextToken());
                sub = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("forced")) {
                forced.put(new Integer(Integer.parseInt(t.nextToken())), new Integer(Integer.parseInt(t.nextToken())));
            } else if (cmd.equals("popupkey")) {
                popupKey = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("loop")) {
                loopPlaylist = Integer.parseInt(t.nextToken());
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
            } else {
                throw new Exception("menu.txt: unknown directive " + cmd);
            }
        }
        r.close();
        if (firstPage == null) throw new Exception("menu.txt: no page");
    }
}
