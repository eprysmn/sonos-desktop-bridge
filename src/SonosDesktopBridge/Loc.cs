using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace SonosDesktopBridge;

/// <summary>
/// Minimal localisation: French and English. The language follows Windows unless the user picks one.
/// XAML: <c>Text="{l:L tab.home}"</c> (live-updating). Code: <c>Loc.T("key", args)</c>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public static string Lang { get; private set; } = Detect();
    public static event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => T(key);

    private static string Detect() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";

    /// <param name="mode">"auto" (follow Windows), "fr" or "en".</param>
    public static void Set(string? mode)
    {
        var lang = mode == "fr" ? "fr" : mode == "en" ? "en" : Detect();
        if (lang == Lang) return;
        Lang = lang;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    public static string T(string key, params object[] args)
    {
        if (!D.TryGetValue(key, out var v)) return key;
        var s = Lang == "fr" ? v[0] : v[1];
        return args.Length == 0 ? s : string.Format(s, args);
    }

    // [key] = { French, English }
    private static readonly Dictionary<string, string[]> D = new()
    {
        // header, tabs, status
        ["app.tagline"] = new[] { "Tes touches volume pilotent tes Sonos", "Your volume keys control your Sonos" },
        ["tab.home"] = new[] { "Accueil", "Home" },
        ["tab.rooms"] = new[] { "Pièces", "Rooms" },
        ["tab.settings"] = new[] { "Réglages", "Settings" },
        ["st.none"] = new[] { "Non connecté", "Not connected" },
        ["st.connected"] = new[] { "Connecté", "Connected" },
        ["st.searching"] = new[] { "Recherche…", "Searching…" },
        ["st.notfound"] = new[] { "Introuvable", "Not found" },
        ["st.error"] = new[] { "Erreur", "Error" },
        ["st.config"] = new[] { "À configurer", "To set up" },
        ["st.roomgone"] = new[] { "Pièce introuvable", "Room not found" },
        ["st.unreach"] = new[] { "Sonos injoignable", "Sonos unreachable" },
        ["st.connfail"] = new[] { "Échec connexion", "Connection failed" },

        // home
        ["sec.room"] = new[] { "Pièce Sonos", "Sonos room" },
        ["btn.search"] = new[] { "Rechercher", "Search" },
        ["hint.searching"] = new[] { "Recherche des lecteurs Sonos sur le réseau local…", "Looking for Sonos players on your local network…" },
        ["sec.volume"] = new[] { "Volume du groupe", "Group volume" },
        ["btn.mute"] = new[] { "Muet", "Mute" },
        ["btn.unmute"] = new[] { "Réactiver", "Unmute" },
        ["btn.refresh"] = new[] { "Actualiser", "Refresh" },

        // keys status card
        ["keys.title"] = new[] { "Touches de volume", "Volume keys" },
        ["keys.pick"] = new[] { "Sortie Windows à choisir", "Windows output to choose" },
        ["keys.pick.sub"] = new[] { "Réglages → Sortie Windows. Sans elle, les touches restent à Windows.", "Settings → Windows output. Without it, the keys stay with Windows." },
        ["keys.wait"] = new[] { "En attente de Sonos", "Waiting for Sonos" },
        ["keys.wait.sub"] = new[] { "Nouvelle tentative automatique toutes les 10 secondes.", "Retrying automatically every 10 seconds." },
        ["keys.off"] = new[] { "Touches désactivées", "Keys disabled" },
        ["keys.off.sub"] = new[] { "Active-les dans Réglages → Touches de volume.", "Turn them on in Settings → Volume keys." },
        ["keys.win"] = new[] { "Touches laissées à Windows", "Keys left to Windows" },
        ["keys.win.out"] = new[] { "Sortie active : {0}.", "Active output: {0}." },
        ["keys.win.src"] = new[] { "Sonos n'est pas sur l'entrée source locale.", "Sonos is not on its local line-in." },
        ["keys.on"] = new[] { "Touches actives", "Keys active" },
        ["keys.on.sub"] = new[] { "+, − et muet pilotent « {0} ».", "+, − and mute control “{0}”." },

        // output hints (settings tab)
        ["out.pick"] = new[] { "Choisis la sortie Windows reliée à ton Sonos : sans elle, les touches de volume restent à Windows.", "Choose the Windows output wired to your Sonos: without it, the volume keys stay with Windows." },
        ["out.wait"] = new[] { "En attente de la connexion Sonos.", "Waiting for the Sonos connection." },
        ["out.off"] = new[] { "Interception des touches désactivée.", "Key interception is turned off." },
        ["out.other"] = new[] { "Sortie Windows active : {0}. Le volume reste géré par Windows (sortie Sonos : {1}).", "Active Windows output: {0}. Volume stays with Windows (Sonos output: {1})." },
        ["out.src"] = new[] { "Sonos n'est pas sur l'entrée source locale : les touches restent à Windows.", "Sonos is not on its local line-in: the keys stay with Windows." },
        ["out.ok"] = new[] { "Cette sortie est active : les touches de volume pilotent Sonos et Windows reste à 100 %.", "This output is active: the volume keys control Sonos and Windows stays at 100%." },
        ["out.unavailable"] = new[] { " (indisponible)", " (unavailable)" },
        ["out.another"] = new[] { "autre sortie", "another output" },

        // rooms tab
        ["sec.roomvol"] = new[] { "Volume par pièce", "Volume per room" },
        ["roomvol.hint"] = new[] { "Lie une autre pièce ci-dessous pour régler leurs volumes séparément.", "Link another room below to set their volumes separately." },
        ["sec.link"] = new[] { "Lier des pièces", "Link rooms" },
        ["link.none"] = new[] { "Aucune autre pièce détectée.", "No other room detected." },
        ["link.note"] = new[] { "Une pièce liée joue la même chose que « ta » pièce. Tu peux ensuite régler son volume séparément ci-dessus. Fonction expérimentale.", "A linked room plays the same thing as “your” room. You can then set its volume separately above. Experimental feature." },

        // settings tab
        ["sec.output"] = new[] { "Sortie Windows", "Windows output" },
        ["sec.keys"] = new[] { "Touches de volume", "Volume keys" },
        ["opt.intercept"] = new[] { "Piloter Sonos avec le clavier", "Control Sonos with the keyboard" },
        ["opt.intercept.d"] = new[] { "Les touches +, − et muet de ton clavier règlent le volume Sonos.", "The +, − and mute keys on your keyboard set the Sonos volume." },
        ["opt.step"] = new[] { "Pas par appui", "Step per press" },
        ["opt.step.d"] = new[] { "De combien le volume change à chaque appui.", "How much the volume changes with each press." },
        ["opt.max"] = new[] { "Volume maximum", "Maximum volume" },
        ["opt.max.d"] = new[] { "Les touches ne dépassent jamais cette limite.", "The keys never go above this limit." },
        ["max.none"] = new[] { "Aucune limite", "No limit" },
        ["opt.linein"] = new[] { "Seulement si Sonos est sur l'entrée source locale", "Only when Sonos is on its local line-in" },
        ["opt.linein.d"] = new[] { "L'entrée source locale (ligne) est celle qui diffuse le son de ton PC. Activé : les touches ne pilotent Sonos que dans ce cas ; si Sonos joue autre chose (Spotify, radio…), elles restent à Windows. Désactivé : elles pilotent Sonos dès que la sortie Windows choisie est active.", "The local line-in is the input that plays your PC's sound. On: the keys control Sonos only in that case; if Sonos plays something else (Spotify, radio…), they stay with Windows. Off: they control Sonos whenever the chosen Windows output is active." },
        ["opt.media"] = new[] { "Touches média (lecture, pause, suivant)", "Media keys (play, pause, next)" },
        ["opt.media.d"] = new[] { "Quand Sonos joue sa propre musique (Spotify, radio…), les touches média le pilotent. Quand Sonos diffuse le son de ton PC, elles restent à ton lecteur Windows.", "When Sonos plays its own music (Spotify, radio…), the media keys control it. When Sonos plays your PC's sound, they stay with your Windows player." },
        ["sec.display"] = new[] { "Affichage et verrouillage", "Display and locking" },
        ["opt.osd"] = new[] { "Indicateur de volume à l'écran", "On-screen volume indicator" },
        ["opt.osd.d"] = new[] { "Affiche brièvement la pièce, la source et le volume quand tu utilises les touches.", "Briefly shows the room, the source and the volume when you use the keys." },
        ["opt.lockroom"] = new[] { "Verrouiller la pièce Sonos", "Lock the Sonos room" },
        ["opt.lockroom.d"] = new[] { "Garde la pièce choisie à chaque démarrage. Désactive pour en changer.", "Keeps the chosen room at every start. Turn off to change it." },
        ["opt.lockout"] = new[] { "Verrouiller la sortie Windows", "Lock the Windows output" },
        ["opt.lockout.d"] = new[] { "Évite de changer par erreur la sortie Windows reliée à Sonos.", "Prevents changing the Windows output wired to Sonos by mistake." },
        ["sec.power"] = new[] { "Mise en veille et démarrage", "Sleep and startup" },
        ["opt.sleep"] = new[] { "Quand le PC se met en veille", "When the PC goes to sleep" },
        ["opt.sleep.d"] = new[] { "Seulement à la vraie mise en veille (pas quand l'écran s'éteint, ni au verrouillage) et seulement si Sonos diffuse le son du PC. Le son est rétabli au réveil.", "Only on a real sleep (not when the screen turns off, nor on lock) and only if Sonos is playing your PC's sound. Sound is restored on wake-up." },
        ["sleep.none"] = new[] { "Ne rien faire", "Do nothing" },
        ["sleep.mute"] = new[] { "Couper le son", "Mute" },
        ["opt.defvol"] = new[] { "Volume au démarrage", "Startup volume" },
        ["opt.defvol.d"] = new[] { "Fixé une fois au lancement de l'app et au réveil du PC, seulement si Sonos diffuse le son du PC. Ne dépasse jamais le volume maximum.", "Set once when the app starts and when the PC wakes up, only if Sonos is playing your PC's sound. Never exceeds the maximum volume." },
        ["defvol.none"] = new[] { "Inchangé", "Unchanged" },
        ["sec.app"] = new[] { "Application", "Application" },
        ["opt.autostart"] = new[] { "Démarrer avec Windows", "Start with Windows" },
        ["opt.autostart.d"] = new[] { "Se lance discrètement dans la zone de notification (icônes cachées), sans ouvrir de fenêtre.", "Starts quietly in the notification area (hidden icons), without opening a window." },
        ["opt.lang"] = new[] { "Langue", "Language" },
        ["opt.lang.d"] = new[] { "Par défaut, celle de Windows.", "By default, the one used by Windows." },
        ["lang.auto"] = new[] { "Automatique", "Automatic" },
        ["lang.fr"] = new[] { "Français", "Français" },
        ["lang.en"] = new[] { "English", "English" },
        ["btn.install"] = new[] { "Installer dans Windows", "Install in Windows" },
        ["btn.uninstall"] = new[] { "Désinstaller", "Uninstall" },
        ["btn.replay"] = new[] { "Revoir la présentation", "Replay the introduction" },
        ["app.installed"] = new[] { "Installée : visible dans le menu Démarrer et dans Paramètres > Applications.", "Installed: shown in the Start menu and in Settings > Apps." },
        ["app.portable"] = new[] { "Version portable. Installe-la pour l'avoir dans le menu Démarrer et dans la liste des applications Windows.", "Portable version. Install it to get it in the Start menu and in the Windows apps list." },
        ["about.version"] = new[] { "Version {0}", "Version {0}" },
        ["about.text"] = new[] { " · Licence MIT · Contrôle local, aucun compte requis · ", " · MIT license · Local control, no account required · " },

        // status messages
        ["hint.noplayers"] = new[] { "Aucun lecteur détecté. Vérifie que le PC et les Sonos sont sur le même réseau local et que le pare-feu autorise la découverte réseau.", "No player found. Check that the PC and the Sonos are on the same local network and that the firewall allows network discovery." },
        ["hint.rooms"] = new[] { "{0} pièce(s) détectée(s).", "{0} room(s) found." },
        ["hint.pickroom"] = new[] { "Choisis la pièce reliée à ton PC. Elle sera mémorisée et verrouillée.", "Choose the room wired to your PC. It will be remembered and locked." },
        ["src.locked"] = new[] { "Pièce verrouillée : {0}", "Locked room: {0}" },
        ["hint.roomgone"] = new[] { "La pièce « {0} » est introuvable (éteinte ou renommée ?). Nouvel essai automatique ; pour en choisir une autre, désactive « Verrouiller la pièce Sonos » dans Réglages.", "The room “{0}” was not found (switched off or renamed?). Retrying automatically; to pick another one, turn off “Lock the Sonos room” in Settings." },
        ["hint.scanfail"] = new[] { "Échec de la découverte : {0}", "Discovery failed: {0}" },
        ["hint.connfail"] = new[] { "Impossible de piloter cette pièce : {0}", "Cannot control this room: {0}" },
        ["hint.control"] = new[] { "Contrôle du groupe « {0} ».", "Controlling the group “{0}”." },
        ["hint.setfail"] = new[] { "Erreur de réglage : {0}", "Could not change the volume: {0}" },
        ["hint.mutefail"] = new[] { "Erreur de sourdine : {0}", "Could not change mute: {0}" },
        ["hint.linkfail"] = new[] { "Liaison impossible : {0}", "Could not link: {0}" },
        ["hint.unlinkfail"] = new[] { "Déliaison impossible : {0}", "Could not unlink: {0}" },
        ["hint.autofail"] = new[] { "Démarrage automatique impossible : {0}", "Could not set start with Windows: {0}" },
        ["app.installfail"] = new[] { "Installation impossible : {0}", "Installation failed: {0}" },
        ["app.uninstallfail"] = new[] { "Désinstallation impossible : {0}", "Uninstall failed: {0}" },
        ["uninstall.confirm"] = new[] { "Désinstaller Sonos Desktop Bridge ?", "Uninstall Sonos Desktop Bridge?" },
        ["shortcut.desc"] = new[] { "Pilote le volume des enceintes Sonos depuis le clavier", "Controls the volume of your Sonos speakers from the keyboard" },

        // sources
        ["src.linein"] = new[] { "Entrée source locale", "Line-in" },
        ["src.tv"] = new[] { "TV", "TV" },
        ["src.stream"] = new[] { "Streaming", "Streaming" },
        ["src.idle"] = new[] { "Aucune lecture", "Nothing playing" },
        ["src.library"] = new[] { "Bibliothèque / AirPlay", "Library / AirPlay" },

        // Sonos errors
        ["err.noroom"] = new[] { "Aucune pièce Sonos sélectionnée.", "No Sonos room selected." },
        ["err.unknownroom"] = new[] { "Pièce inconnue.", "Unknown room." },
        ["err.nogroup"] = new[] { "Groupe introuvable.", "Group not found." },
        ["err.topology"] = new[] { "Topologie Sonos illisible.", "Sonos topology unreadable." },
        ["err.nogroups"] = new[] { "Aucun groupe Sonos trouvé.", "No Sonos group found." },
        ["err.noplayer"] = new[] { "Aucun lecteur Sonos joignable.", "No Sonos player reachable." },
        ["err.unreach"] = new[] { "Sonos injoignable.", "Sonos unreachable." },
        ["err.roomgone"] = new[] { "Pièce « {0} » introuvable.", "Room “{0}” not found." },

        // tray and OSD
        ["tray.open"] = new[] { "Ouvrir", "Open" },
        ["tray.mute"] = new[] { "Muet / Réactiver", "Mute / Unmute" },
        ["tray.quit"] = new[] { "Quitter", "Quit" },
        ["tray.hint"] = new[] { "L'application reste active dans la zone de notification (icônes cachées).", "The app keeps running in the notification area (hidden icons)." },
        ["tray.ready"] = new[] { "Prêt. L'application tourne dans la zone de notification.", "Ready. The app is running in the notification area." },
        ["osd.play"] = new[] { "Lecture", "Play" },
        ["osd.pause"] = new[] { "Pause", "Pause" },
        ["osd.next"] = new[] { "Suivant", "Next" },
        ["osd.prev"] = new[] { "Précédent", "Previous" },
        ["osd.mute"] = new[] { "Muet", "Muted" },

        // setup wizard
        ["w.skip"] = new[] { "Passer", "Skip" },
        ["w.welcome"] = new[] { "Bienvenue", "Welcome" },
        ["w.welcome.sub"] = new[] { "Sonos Desktop Bridge branche les touches volume de ton clavier sur tes enceintes Sonos.", "Sonos Desktop Bridge connects your keyboard's volume keys to your Sonos speakers." },
        ["w.b1"] = new[] { "Les touches + − et muet règlent le vrai volume de Sonos, plus celui de Windows.", "The + − and mute keys set the real Sonos volume, not the Windows one." },
        ["w.b2"] = new[] { "Un indicateur s'affiche à l'écran avec la pièce, la source et le volume.", "An on-screen indicator shows the room, the source and the volume." },
        ["w.b3"] = new[] { "Si tu passes sur un casque Bluetooth, Windows reprend automatiquement la main.", "If you switch to a Bluetooth headset, Windows takes over automatically." },
        ["w.steps"] = new[] { "Configuration en 3 étapes · environ une minute", "Setup in 3 steps · about a minute" },
        ["w.room.t"] = new[] { "Quelle pièce Sonos est reliée à ton PC ?", "Which Sonos room is wired to your PC?" },
        ["w.room.h"] = new[] { "Choisis la pièce qui reçoit le son de ton ordinateur (câble ou adaptateur). Elle sera mémorisée.", "Choose the room that receives your computer's sound (cable or adapter). It will be remembered." },
        ["w.rescan"] = new[] { "Rechercher à nouveau", "Search again" },
        ["w.hint.none"] = new[] { "Aucune pièce détectée pour l'instant. Vérifie que le PC et les Sonos sont sur le même réseau, puis relance la recherche.", "No room found yet. Check that the PC and the Sonos are on the same network, then search again." },
        ["w.hint.sugg"] = new[] { "Suggestion : « {0} » joue en ce moment l'entrée source locale, c'est-à-dire le son de ton PC.", "Suggestion: “{0}” is currently playing its line-in, which is your PC's sound." },
        ["w.hint.tip"] = new[] { "Astuce : la pièce reliée à ton PC est celle qui joue l'entrée source locale.", "Tip: the room wired to your PC is the one playing its line-in." },
        ["w.out.t"] = new[] { "Quelle sortie audio Windows envoie le son vers Sonos ?", "Which Windows audio output sends sound to Sonos?" },
        ["w.out.h"] = new[] { "C'est la sortie branchée à l'adaptateur Sonos (souvent la prise jack). Windows sera gardé à 100 % sur cette sortie : seules les touches comptent.", "It is the output plugged into the Sonos adapter (often the headphone jack). Windows will be kept at 100% on that output: only the keys matter." },
        ["w.out.active"] = new[] { "Sortie active en ce moment : {0}.", "Currently active output: {0}." },
        ["w.usecurrent"] = new[] { "Utiliser la sortie active en ce moment", "Use the currently active output" },
        ["w.prefs.t"] = new[] { "Dernières préférences", "Last preferences" },
        ["w.prefs.h"] = new[] { "Tu pourras tout changer plus tard dans les réglages.", "You can change everything later in the settings." },
        ["w.lock"] = new[] { "Verrouiller ces choix", "Lock these choices" },
        ["w.lock.d"] = new[] { "La pièce et la sortie ne changent plus par accident.", "The room and the output will not change by accident." },
        ["w.install"] = new[] { "Installer dans Windows", "Install in Windows" },
        ["w.install.d"] = new[] { "Ajoute l'application au menu Démarrer et à la liste des applications installées.", "Adds the app to the Start menu and to the installed apps list." },
        ["w.done.t"] = new[] { "Tout est prêt", "All set" },
        ["w.done.try"] = new[] { "Essaie maintenant : appuie sur une touche volume de ton clavier.", "Try it now: press a volume key on your keyboard." },
        ["w.done.tray"] = new[] { "L'application reste dans la zone de notification, près de l'horloge (clique sur la flèche ^ pour voir les icônes cachées). Clic gauche : ouvrir. Clic droit : quitter.", "The app stays in the notification area near the clock (click the ^ arrow to see hidden icons). Left-click: open. Right-click: quit." },
        ["w.start"] = new[] { "Commencer", "Get started" },
        ["w.continue"] = new[] { "Continuer", "Continue" },
        ["w.finish"] = new[] { "Terminer", "Finish" },
        ["w.back"] = new[] { "Retour", "Back" },
        ["w.sum.room"] = new[] { "Pièce Sonos : {0}", "Sonos room: {0}" },
        ["w.sum.out"] = new[] { "Sortie Windows : {0}", "Windows output: {0}" },
        ["w.sum.auto"] = new[] { "Démarrage avec Windows : {0}", "Start with Windows: {0}" },
        ["w.sum.osd"] = new[] { "Indicateur à l'écran : {0}", "On-screen indicator: {0}" },
        ["w.sum.lock"] = new[] { "Choix verrouillés : {0}", "Choices locked: {0}" },
        ["yes"] = new[] { "oui", "yes" },
        ["no"] = new[] { "non", "no" },
    };
}

/// <summary>XAML markup extension: <c>{l:L key}</c> binds to the live translation of <c>key</c>.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LExtension : MarkupExtension
{
    public LExtension(string key) { Key = key; }

    [ConstructorArgument("key")]
    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
