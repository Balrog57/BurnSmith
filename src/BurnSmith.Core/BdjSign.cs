using System.Buffers.Binary;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace BurnSmith.Core;

/// <summary>
/// Signs the BD-J menu the way a player checks it, with the layout of the HD Cookbook tools that DVDFab's signed menus also carry: a self-signed disc root
/// certificate in CERTIFICATE\ (with bu.discroot.crt, id.bdmv and their BACKUP copies), a leaf certificate whose O= ends with the organisation ID, and
/// META-INF\SIG-BD00.SF / .RSA in the jar. The BDJO's application ID 0x4000 is a signed application: a player does not start it unsigned (VLC never checks).
/// </summary>
public static class BdjSign
{
    /// <summary>The organisation ID shared by the BDJO, id.bdmv and the certificates ("BST0").</summary>
    public const uint OrgId = 0x42535430;

    const string Sha1 = "1.3.14.3.2.26", Sha1Rsa = "1.2.840.113549.1.1.5", RsaKey = "1.2.840.113549.1.1.1";

    /// <summary>Writes <paramref name="jar"/> with its entries, signed, and the CERTIFICATE folder of the disc in <paramref name="discRoot"/> (the folder holding BDMV).</summary>
    public static void Write(string jar, IReadOnlyList<(string Name, byte[] Data)> entries, string discRoot)
    {
        // 1024-bit RSA with SHA-1: the profile the players of 2006 check, the one DVDFab's menus carry
        using var rootKey = RSA.Create(1024);
        using var leafKey = RSA.Create(1024);
        long rootSerial = RandomNumberGenerator.GetInt32(1, int.MaxValue), leafSerial = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var root = Certificate("BurnSmith", rootSerial, rootKey, rootKey, X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true, "root@burnsmith.invalid");
        var bu = Certificate("BurnSmith", rootSerial + 1, rootKey, rootKey, X509KeyUsageFlags.DigitalSignature, true, "root@burnsmith.invalid");
        var leaf = Certificate("BurnSmith Menu", leafSerial, leafKey, rootKey, X509KeyUsageFlags.DigitalSignature, false, "menu@burnsmith.invalid");

        var manifest = new StringBuilder("Manifest-Version: 1.0\r\nCreated-By: BurnSmith\r\n\r\n");
        var digests = new StringBuilder();
        foreach (var (name, data) in entries)
        {
            var section = Line("Name: " + name) + Line("SHA1-Digest: " + Convert.ToBase64String(SHA1.HashData(data))) + "\r\n";
            manifest.Append(section);
            digests.Append(Line("Name: " + name)).Append(Line("SHA1-Digest: " + Convert.ToBase64String(SHA1.HashData(Encoding.UTF8.GetBytes(section))))).Append("\r\n");
        }
        var mf = Encoding.UTF8.GetBytes(manifest.ToString());
        var sf = Encoding.UTF8.GetBytes("Signature-Version: 1.0\r\nCreated-By: BurnSmith\r\nBDJ-Signature-Version: 1.0\r\n"
                                        + Line("SHA1-Digest-Manifest: " + Convert.ToBase64String(SHA1.HashData(mf))) + "\r\n" + digests);
        using (var z = ZipFile.Open(jar, ZipArchiveMode.Create))
            foreach (var (name, data) in new[] { ("META-INF/MANIFEST.MF", mf), ("META-INF/SIG-BD00.SF", sf), ("META-INF/SIG-BD00.RSA", SignedData(sf, leaf, root, leafSerial, leafKey)) }.Concat(entries))
            {
                using var to = z.CreateEntry(name, CompressionLevel.Optimal).Open();
                to.Write(data);
            }

        var id = new byte[89]; // the length HD Cookbook's id.bdmv has
        "BDID0200"u8.CopyTo(id);
        BinaryPrimitives.WriteUInt32BigEndian(id.AsSpan(40), OrgId);
        RandomNumberGenerator.Fill(id.AsSpan(44, 16)); // disc ID: a player keeps what it saves per disc under it
        foreach (var dir in new[] { "CERTIFICATE", @"CERTIFICATE\BACKUP" })
        {
            Directory.CreateDirectory(Path.Combine(discRoot, dir));
            File.WriteAllBytes(Path.Combine(discRoot, dir, "app.discroot.crt"), root);
            File.WriteAllBytes(Path.Combine(discRoot, dir, "bu.discroot.crt"), bu);
            File.WriteAllBytes(Path.Combine(discRoot, dir, "id.bdmv"), id);
        }
    }

    /// <summary>
    /// What a player would refuse in the signature of <paramref name="jar"/>: an application certificate the disc root (<paramref name="discRoot"/>) did not issue,
    /// SIG-BD00.SF not signed by it, a file whose digest no longer matches or that the manifest leaves out, an organisation ID other than id.bdmv's.
    /// </summary>
    public static List<string> Verify(string jar, byte[] discRoot, byte[] idBdmv)
    {
        var shown = "JAR\\" + Path.GetFileName(jar);
        var problems = new List<string>();
        try
        {
            using var z = ZipFile.OpenRead(jar);
            byte[] Read(ZipArchiveEntry e) { using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
            byte[] Meta(string name) => Read(z.GetEntry("META-INF/" + name) ?? throw new InvalidDataException("META-INF/" + name + " manque"));
            var (mf, sf, rsa) = (Meta("MANIFEST.MF"), Meta("SIG-BD00.SF"), Meta("SIG-BD00.RSA"));

            // PKCS#7: ContentInfo { type, [0] SignedData { version, digest algorithms, content, [0] certificates, signer infos } }
            var info = new AsnReader(rsa, AsnEncodingRules.BER).ReadSequence();
            info.ReadObjectIdentifier();
            var signed = info.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            signed.ReadInteger(); signed.ReadSetOf(); signed.ReadSequence();
            var certs = signed.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
            var all = new List<byte[]>();
            while (certs.HasData) all.Add(certs.ReadEncodedValue().ToArray());
            var leaf = all.FirstOrDefault(c => !c.AsSpan().SequenceEqual(discRoot)) ?? throw new InvalidDataException("pas de certificat du menu");
            var signer = signed.ReadSetOf().ReadSequence();
            signer.ReadInteger(); signer.ReadSequence(); signer.ReadSequence(); signer.ReadSequence(); // version, issuer and serial, digest, key algorithm
            var signature = signer.ReadOctetString();

            var (root, app) = (Parse(discRoot), Parse(leaf));
            using var rootKey = root.Key;
            using var appKey = app.Key;
            if (!rootKey.VerifyData(app.Signed.Span, app.Signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1))
                problems.Add($"{shown} : le certificat du menu n'est pas signé par CERTIFICATE\\app.discroot.crt");
            if (!appKey.VerifyData(sf, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1))
                problems.Add($"{shown} : la signature de SIG-BD00.SF est fausse");
            var org = Regex.Match(app.Org, @"\.([0-9a-fA-F]{8})$").Groups[1].Value;
            if (!org.Equals(BinaryPrimitives.ReadUInt32BigEndian(idBdmv.AsSpan(40)).ToString("x8"), StringComparison.OrdinalIgnoreCase))
                problems.Add($"{shown} : l'organisation du certificat ({org}) n'est pas celle de CERTIFICATE\\id.bdmv");
            if (Sections(sf).GetValueOrDefault("")?.GetValueOrDefault("SHA1-Digest-Manifest") != Convert.ToBase64String(SHA1.HashData(mf)))
                problems.Add($"{shown} : SIG-BD00.SF ne correspond pas au manifeste");
            var listed = Sections(mf);
            foreach (var e in z.Entries.Where(e => e.Length > 0 && !e.FullName.StartsWith("META-INF/", StringComparison.Ordinal)))
                if (!listed.TryGetValue(e.FullName, out var section) || section.GetValueOrDefault("SHA1-Digest") != Convert.ToBase64String(SHA1.HashData(Read(e))))
                    problems.Add($"{shown} : {e.FullName} n'est pas signé tel qu'il est");
        }
        catch (Exception e) when (e is InvalidDataException or AsnContentException or CryptographicException or ArgumentException or IOException)
        {
            problems.Add($"{shown} : signature illisible ({e.Message})");
        }
        return problems;
    }

    /// <summary>
    /// What a player takes from a certificate: the part signed, its signature, the public key, the O= of the subject. Read by hand: Windows cannot load a
    /// certificate valid from year 1 (DVDFab's neither), its dates being counted from 1601.
    /// </summary>
    static (ReadOnlyMemory<byte> Signed, byte[] Signature, RSA Key, string Org) Parse(byte[] certificate)
    {
        var cert = new AsnReader(certificate, AsnEncodingRules.BER).ReadSequence();
        var signed = cert.ReadEncodedValue();
        cert.ReadSequence();
        var signature = cert.ReadBitString(out _);
        var tbs = new AsnReader(signed, AsnEncodingRules.BER).ReadSequence();
        if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0))) tbs.ReadEncodedValue(); // version
        tbs.ReadInteger(); tbs.ReadSequence(); tbs.ReadSequence(); tbs.ReadSequence(); // serial, algorithm, issuer, validity
        var subject = tbs.ReadSequence();
        var org = "";
        while (subject.HasData)
        {
            var part = subject.ReadSetOf().ReadSequence();
            if (part.ReadObjectIdentifier() == "2.5.4.10") org = part.ReadCharacterString((UniversalTagNumber)part.PeekTag().TagValue);
        }
        var key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(tbs.ReadEncodedValue().Span, out _);
        return (signed, signature, key, org);
    }

    /// <summary>A manifest line: at most 72 bytes with its CRLF, a longer value going on after a space on the next line.</summary>
    static string Line(string text)
    {
        var b = new StringBuilder();
        var room = 70;
        foreach (var r in text.EnumerateRunes())
        {
            if (r.Utf8SequenceLength > room) { b.Append("\r\n "); room = 69; }
            b.Append(r.ToString());
            room -= r.Utf8SequenceLength;
        }
        return b.Append("\r\n").ToString();
    }

    /// <summary>The sections of a manifest or .SF by name ("" for the main one), each with its attributes.</summary>
    static Dictionary<string, Dictionary<string, string>> Sections(byte[] file)
    {
        var all = new Dictionary<string, Dictionary<string, string>>();
        foreach (var block in Encoding.UTF8.GetString(file).Replace("\r\n", "\n").Replace("\n ", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = new Dictionary<string, string>();
            foreach (var line in block.Split('\n'))
                if (line.IndexOf(": ", StringComparison.Ordinal) is > 0 and var at) fields[line[..at]] = line[(at + 2)..];
            all[fields.GetValueOrDefault("Name", "")] = fields;
        }
        return all;
    }

    static void Algorithm(AsnWriter w, string oid)
    {
        using (w.PushSequence()) { w.WriteObjectIdentifier(oid); w.WriteNull(); }
    }

    static void Name(AsnWriter w, string cn)
    {
        using (w.PushSequence())
            foreach (var (oid, value) in new[] { ("2.5.4.3", cn), ("2.5.4.11", "BurnSmith"), ("2.5.4.10", $"BDJCompany.{OrgId:x8}"), ("2.5.4.6", "FR") })
                using (w.PushSetOf())
                using (w.PushSequence()) { w.WriteObjectIdentifier(oid); w.WriteCharacterString(UniversalTagNumber.UTF8String, value); }
    }

    /// <summary>An X.509 v3 certificate issued by the disc root ("BurnSmith"), valid from year 1 to 9999 since a player's clock may say anything.</summary>
    static byte[] Certificate(string cn, long serial, RSA key, RSA issuerKey, X509KeyUsageFlags usage, bool ca, string mail)
    {
        var alt = new SubjectAlternativeNameBuilder();
        alt.AddEmailAddress(mail);
        var issuerAlt = new SubjectAlternativeNameBuilder();
        issuerAlt.AddEmailAddress("root@burnsmith.invalid");
        var extensions = new List<X509Extension> { new X509KeyUsageExtension(usage, true) };
        if (ca) extensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        extensions.Add(alt.Build());
        extensions.Add(new X509Extension("2.5.29.18", issuerAlt.Build().RawData, false));

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true))) w.WriteInteger(2); // v3
            w.WriteInteger(serial);
            Algorithm(w, Sha1Rsa);
            Name(w, "BurnSmith");
            using (w.PushSequence())
            {
                w.WriteGeneralizedTime(new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero));
                w.WriteGeneralizedTime(new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero));
            }
            Name(w, cn);
            w.WriteEncodedValue(key.ExportSubjectPublicKeyInfo());
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 3, true)))
            using (w.PushSequence())
                foreach (var e in extensions)
                    using (w.PushSequence())
                    {
                        w.WriteObjectIdentifier(e.Oid!.Value!);
                        if (e.Critical) w.WriteBoolean(true);
                        w.WriteOctetString(e.RawData);
                    }
        }
        var tbs = w.Encode();
        var c = new AsnWriter(AsnEncodingRules.DER);
        using (c.PushSequence())
        {
            c.WriteEncodedValue(tbs);
            Algorithm(c, Sha1Rsa);
            c.WriteBitString(issuerKey.SignData(tbs, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
        }
        return c.Encode();
    }

    /// <summary>The detached PKCS#7 signature of the .SF file as jarsigner writes it: no signed attributes, the leaf then the root certificate.</summary>
    static byte[] SignedData(byte[] sf, byte[] leaf, byte[] root, long leafSerial, RSA leafKey)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier("1.2.840.113549.1.7.2");
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (w.PushSequence())
            {
                w.WriteInteger(1);
                using (w.PushSetOf()) Algorithm(w, Sha1);
                using (w.PushSequence()) w.WriteObjectIdentifier("1.2.840.113549.1.7.1");
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true))) { w.WriteEncodedValue(leaf); w.WriteEncodedValue(root); } // a SET in DER would sort them
                using (w.PushSetOf())
                using (w.PushSequence())
                {
                    w.WriteInteger(1);
                    using (w.PushSequence()) { Name(w, "BurnSmith"); w.WriteInteger(leafSerial); }
                    Algorithm(w, Sha1);
                    Algorithm(w, RsaKey);
                    w.WriteOctetString(leafKey.SignData(sf, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
                }
            }
        }
        return w.Encode();
    }
}
