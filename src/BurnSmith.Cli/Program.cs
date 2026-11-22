using BurnSmith.Cli;

var a = args.ToList();
if (a.Count == 0 || a[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("""
        BurnSmith CLI — authoring DVD / Blu-ray / 4K UHD / AVCHD

        Fabrication :
          burnsmith bd|dvd --video <fichier> [options vidéo...] --out <dossier> [options disque...]
          burnsmith project <fichier.burnsmith> [--out <dossier>] [--trial [S]] [--max-compat] [--recap] [--shutdown]
          burnsmith volumes <fichier.burnsmith> [--split N1,N2,...] [--out <dossier>] [--build]
          burnsmith queue <a.burnsmith> <b.burnsmith> ... [--trial [S]] [--shutdown]

        Options vidéo (après chaque --video) :
          --name TEXTE | --trim A-B[,C-D] | --volume N | --bonus | --intro
          --audio N,N | --default-audio N | --audio-file [LANG=]FICHIER | --audio-delay N=S | --audio-volume N=DB
          --sub N,N|none | --default-sub N | --sub-file [LANG=]FICHIER | --sub-delay N=S | --track-lang a0=fre,s1=eng
          --chapter-at T,T | --chapters-file FICHIER(.txt|.xml) | --chapter-names "A|B" | --chapter-every MIN | --no-chapters

        Options disque (bd / dvd) :
          --uhd | --avchd | --profile NOM | --capacity GO | --quality fit|source|N | --reencode | --cpu | --fps R
          --menu-mode bdj|hdmv | --design NOM|FICHIER.json | --title TEXTE | --no-menu | --popup | --loop | --music FICHIER
          --loudnorm | --hdr-nits N | --no-lossless | --no-copy-eac3 | --play-mode 0|1|2 | --auto-start S | --remember-position
          --sub-font NOM | --sub-size % | --sub-color RRGGBB | --sub-position 0-100
          --preview [--safe-area] | --recap | --trial [S] | --save-project FICHIER | --max-compat | --shutdown

        ISO, gravure & outils :
          burnsmith drives | iso <dossier> <out.iso> [--label X] [--dvd] | burn <image.iso> <F:> [--speed N] [--no-verify] [--eject] [--erase]
          burnsmith verify <image.iso> <F:> | erase <F:> | play <dossier|iso> | probe <vidéo>
          burnsmith check-disc <dossier|iso> | check-navigation <dossier|iso> | scan-damage <dossier|iso>
          burnsmith slideshow|song|join ... | templates | preview <modèle> <out> | make-qualif-pack --out <dossier>
        """);
    return 0;
}

return a[0] switch
{
    "bd" or "dvd" => CliBuild.RunBuild(a),
    "project" => CliBuild.RunProject(a),
    "volumes" => CliBuild.RunVolumes(a),
    "queue" => CliBuild.RunQueue(a),

    "drives" => CliDisc.RunDrives(a),
    "check-drives" => CliDisc.RunCheckDrives(),
    "iso" => CliDisc.RunIso(a),
    "burn" => CliDisc.RunBurn(a),
    "erase" => CliDisc.RunErase(a),
    "verify" => CliDisc.RunVerify(a),
    "play" => CliDisc.RunPlay(a),
    "check-play" => CliDisc.RunCheckPlay(),
    "check-disc" => CliDisc.RunCheckDisc(a),
    "check-navigation" => CliDisc.RunCheckNavigation(a),
    "scan-damage" => CliDisc.RunScanDamage(a),
    "check-scan-damage" => CliDisc.RunCheckScanDamage(),
    "probe" => CliDisc.RunProbe(a),
    "repair-top-menu" => CliDisc.RunRepairTopMenu(a),
    "replace-film" => CliDisc.RunReplaceFilm(a),
    "check-film-sync" => CliDisc.RunCheckFilmSync(),

    "preview" => CliTools.RunPreview(a),
    "dump" => CliTools.RunDump(a),
    "slideshow" or "song" or "join" => CliTools.RunMediaMaker(a),
    "check-vectors" => CliTools.RunCheckVectors(a),
    "svg-sheet" => CliTools.RunSvgSheet(a),
    "pack-templates" => CliTools.RunPackTemplates(a),
    "convert-templates" => CliTools.RunConvertTemplates(a),
    "check-conversion" => CliTools.RunCheckConversion(),
    "check-library" => CliTools.RunCheckLibrary(),
    "templates" => CliTools.RunTemplates(a),
    "design-export" => CliTools.RunDesignExport(a),
    "template-save" => CliTools.RunTemplateSave(a),
    "import-background" or "import-button" => CliTools.RunImportAsset(a),
    "label-from-scan" => CliTools.RunLabelFromScan(a),
    "hold-tool" => CliTools.RunHoldTool(a),

    "make-qualif-pack" => QualifPack.Run(CliBuild.Opt(a, "--out") ?? throw new ArgumentException("make-qualif-pack requiert --out <dossier>")),
    "check-plan" => PlanChecks.Run(),
    "check-bd" => BdCheck.Run(a.Contains("--no-dvd")),
    "check-reliability" => ReliabilityCheck.Run(a.Contains("--no-dvd")),
    "check-recap" => RecapCheck.Run(),
    "check-templates" => TemplateCheck.Run(CliBuild.Opt(a, "--picture") is { } cp ? Path.GetFullPath(cp) : null, a.Contains("--matrix"), CliBuild.Opt(a, "--only")),
    "check-fidelity" => TemplateCheck.Fidelity(CliBuild.Opt(a, "--picture") is { } fp ? Path.GetFullPath(fp) : null, CliBuild.Opt(a, "--only")),
    "check-highlights" => HighlightsCheck.Run(CliBuild.Opt(a, "--out")),
    "check-elements" => ElementsCheck.Run(),
    "check-update" => await UpdateCheck.Run(a.Contains("--live")),
    "check-edit" => EditCheck.Run(a.Contains("--no-dvd"), CliBuild.SubsToKeep),

    _ => CliBuild.Usage(),
};
