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
 *   page ID BACKGROUND_IMAGE
 *   button PAGE ID X Y W H FOCUS_IMAGE LEFT RIGHT UP DOWN ACTION [ARG1 [ARG2]]
 *   marker IMAGE W H DX
 *   loop PLAYLIST
 *   playback menu|first CHAIN REWIND FIRST_PLAYLIST COUNT   (CHAIN, REWIND: 1 or 0; absent = menu, no chaining, back to the menu)
 *   defaults AUDIO SUB   (the audio / subtitle stream number the disc starts with; absent = 1 and 0, no subtitles)
 * ACTION is "play PLAYLIST", "chapter PLAYLIST MARK", "page PAGE", "playall FIRST_PLAYLIST COUNT" (plays them one
 * after the other), "audio N" (stream number, 1-based) or "sub N" (0 = no subtitles). The first page is shown
 * first and the first button of a page has the initial focus. The marker image is drawn DX pixels from the left
 * edge of the "audio"/"sub" button that matches the current choice. Audio and subtitle choices are applied to the
 * player each time a playlist starts.
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
        Vector buttons = new Vector();
    }

    private class View extends Container {
        public void paint(Graphics g) {
            Page p = page;
            Button b = focus;
            if (loopPlaylist > 0) clear(g);
            if (p == null) return;
            if (p.bg != null) g.drawImage(p.bg, 0, 0, this);
            if (b != null && b.focus != null) g.drawImage(b.focus, b.x, b.y, this);
            if (marker == null) return;
            for (int i = 0; i < p.buttons.size(); i++) {
                Button m = (Button) p.buttons.elementAt(i);
                boolean on = (m.action.equals("audio") && m.n1 == audio) || (m.action.equals("sub") && m.n1 == sub);
                if (on) g.drawImage(marker, m.x + markerDx, m.y + (m.h - markerH) / 2, this);
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
    private Image marker;
    private String markerName;
    private int markerW, markerH, markerDx;
    private int nextPlaylist, endPlaylist; // "play all": playlists still to come, [next, end)
    // "playback" line: where the disc starts and what happens when a title ends (see finished())
    private boolean startFirst, chain, rewind = true; // start on the first title / titles follow each other / back to the start afterwards
    private int titleFirst, titleCount; // the titles of the disc: playlists [first, first + count)

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
        int n = 0;
        marker = load(markerName, tracker, n++);
        for (java.util.Enumeration e = pages.elements(); e.hasMoreElements();) {
            Page p = (Page) e.nextElement();
            p.bg = load(p.bgName, tracker, n++);
            for (int i = 0; i < p.buttons.size(); i++) {
                Button b = (Button) p.buttons.elementAt(i);
                b.focus = load(b.focusName, tracker, n++);
            }
        }
        tracker.waitForAll();
        lastButton = readGpr(108) - 1; // the register holds index + 1, so 0 means "nothing remembered"
        if (startFirst && titleCount > 0 && firstPlayback()) startSequence();
        else showMenu();
    }

    /**
     * The player's Top Menu key restarts this same xlet: the disc only starts on the first title the first time (title number register
     * PSR4 is 0xFFFF in First Playback and 0 once the Top Menu is called), so the menu stays reachable.
     */
    private boolean firstPlayback() {
        try {
            return RegisterAccess.getInstance().getPSR(4) == 0xFFFF;
        } catch (Throwable t) {
            log("title number register unreadable: " + t);
            return true;
        }
    }

    /** "Start from the first title": no menu, the titles play one after the other. */
    private void startSequence() {
        nextPlaylist = titleFirst + 1;
        endPlaylist = titleFirst + titleCount;
        play(titleFirst, -1);
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
            showMenu();
        } else {
            log("playback finished: stopped");
        }
    }

    private void showMenu() {
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
        page = p;
        focus = p.buttons.isEmpty() ? null : (Button) p.buttons.elementAt(0);
        // back from a title: the button that started it is focused again, from GPR108
        if (p == firstPage && lastButton >= 0 && lastButton < p.buttons.size()) {
            focus = (Button) p.buttons.elementAt(lastButton);
            log("resume on button " + lastButton);
        }
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
        EventManager.getInstance().addUserEventListener(this, keys);
        listening = true;
    }

    private void stopListening() {
        if (!listening) return;
        EventManager.getInstance().removeUserEventListener(this);
        listening = false;
    }

    public void userEventReceived(UserEvent e) {
        if (e.getType() != KeyEvent.KEY_PRESSED || focus == null) return;
        switch (e.getCode()) {
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
            follow(b.n1);
            play(b.n1, -1);
        } else if (b.action.equals("chapter")) {
            remember(b);
            follow(b.n1);
            play(b.n1, b.n2);
        } else if (b.action.equals("playall")) {
            remember(b);
            nextPlaylist = b.n1 + 1;
            endPlaylist = b.n1 + b.n2;
            play(b.n1, -1);
        } else if (b.action.equals("audio")) {
            audio = b.n1;
            log("audio choice " + audio);
            view.repaint();
        } else if (b.action.equals("sub")) {
            sub = b.n1;
            log("subtitle choice " + sub);
            view.repaint();
        }
    }

    /** Remembers where the viewer started from, so the menu reopens on that button (GPR108). */
    private void remember(Button b) {
        if (page != firstPage) return; // the index only means something on the page the menu reopens on
        int i = page.buttons.indexOf(b);
        if (i < 0) return;
        lastButton = i;
        writeGpr(108, i + 1);
    }

    /** The title picked in the menu is followed by the next ones when the disc plays everything in a row. */
    private void follow(int playlist) {
        nextPlaylist = endPlaylist = 0;
        if (chain && titleCount > 0 && playlist >= titleFirst && playlist < titleFirst + titleCount) {
            nextPlaylist = playlist + 1;
            endPlaylist = titleFirst + titleCount;
        }
    }

    /** Applies the audio / subtitle choice to a playlist that is running. Failures only cost the choice, never the playback. */
    private void applyTracks(Player p) {
        try {
            Control c = p.getControl("org.bluray.media.PrimaryAudioControl");
            if (c instanceof PrimaryAudioControl) {
                ((PrimaryAudioControl) c).selectStreamNumber(audio);
                log("audio stream " + audio + " selected");
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
                if (sub > 0) {
                    s.selectStreamNumber(sub);
                    s.setSubtitling(true);
                } else {
                    s.setSubtitling(false);
                }
                log("subtitle stream " + sub + " selected");
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
            scene.setVisible(false);
            stopListening();
            stopLoop();
            player = Manager.createPlayer(new MediaLocator(loc.toExternalForm()));
            player.addControllerListener(this);
            player.start();
        } catch (Exception ex) {
            ex.printStackTrace();
            closePlayer();
            showMenu();
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
            if (e instanceof EndOfMediaEvent && nextPlaylist > 0 && nextPlaylist < endPlaylist) play(nextPlaylist++, -1);
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
            } else if (cmd.equals("loop")) {
                loopPlaylist = Integer.parseInt(t.nextToken());
            } else if (cmd.equals("page")) {
                Page p = new Page();
                p.id = t.nextToken();
                p.bgName = dash(t.nextToken());
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
