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

import javax.media.ControllerErrorEvent;
import javax.media.ControllerEvent;
import javax.media.ControllerListener;
import javax.media.EndOfMediaEvent;
import javax.media.Manager;
import javax.media.MediaLocator;
import javax.media.Player;
import javax.tv.xlet.Xlet;
import javax.tv.xlet.XletContext;
import javax.tv.xlet.XletStateChangeException;

import org.bluray.net.BDLocator;
import org.dvb.event.EventManager;
import org.dvb.event.UserEvent;
import org.dvb.event.UserEventListener;
import org.dvb.event.UserEventRepository;
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
 * ACTION is "play PLAYLIST", "chapter PLAYLIST MARK" or "page PAGE". The first page is shown first
 * and the first button of a page has the initial focus.
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
            if (p == null) return;
            if (p.bg != null) g.drawImage(p.bg, 0, 0, this);
            if (b != null && b.focus != null) g.drawImage(b.focus, b.x, b.y, this);
        }
    }

    private XletContext ctx;
    private HScene scene;
    private View view;
    private Player player;
    private final Hashtable pages = new Hashtable();
    private Page firstPage, page;
    private Button focus;
    private int width = 1920, height = 1080;
    private boolean listening;

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
        for (java.util.Enumeration e = pages.elements(); e.hasMoreElements();) {
            Page p = (Page) e.nextElement();
            p.bg = load(p.bgName, tracker, n++);
            for (int i = 0; i < p.buttons.size(); i++) {
                Button b = (Button) p.buttons.elementAt(i);
                b.focus = load(b.focusName, tracker, n++);
            }
        }
        tracker.waitForAll();
        showMenu();
    }

    private void showMenu() {
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
        view.repaint();
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
            play(b.n1, -1);
        } else if (b.action.equals("chapter")) {
            play(b.n1, b.n2);
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
            player = Manager.createPlayer(new MediaLocator(loc.toExternalForm()));
            player.addControllerListener(this);
            player.start();
        } catch (Exception ex) {
            ex.printStackTrace();
            closePlayer();
            showMenu();
        }
    }

    /** End of the playlist (or a playback error): back to the menu. */
    public void controllerUpdate(ControllerEvent e) {
        if (e instanceof EndOfMediaEvent || e instanceof ControllerErrorEvent) {
            log("playback ended: " + e.getClass().getName());
            closePlayer();
            showMenu();
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
