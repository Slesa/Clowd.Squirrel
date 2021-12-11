﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Squirrel.MarkdownSharp;
using Squirrel.NuGet;
using Squirrel.SimpleSplat;
using System.Threading.Tasks;
using SharpCompress.Archives.Zip;
using SharpCompress.Readers;
using Squirrel;

namespace SquirrelCli
{
    internal class ReleasePackageBuilder : ReleasePackage
    {
        public ReleasePackageBuilder(string inputPackageFile, bool isReleasePackage = false) : base(inputPackageFile, isReleasePackage)
        {
        }

        public string CreateReleasePackage(string outputFile, Func<string, string> releaseNotesProcessor = null, Action<string> contentsPostProcessHook = null)
        {
            Contract.Requires(!String.IsNullOrEmpty(outputFile));
            releaseNotesProcessor = releaseNotesProcessor ?? (x => (new Markdown()).Transform(x));

            if (ReleasePackageFile != null) {
                return ReleasePackageFile;
            }

            var package = new ZipPackage(InputPackageFile);

            var dontcare = default(SemanticVersion);

            // NB: Our test fixtures use packages that aren't SemVer compliant, 
            // we don't really care that they aren't valid
            if (!ModeDetector.InUnitTestRunner() && !SemanticVersion.TryParseStrict(package.Version.ToString(), out dontcare)) {
                throw new Exception(
                    String.Format(
                        "Your package version is currently {0}, which is *not* SemVer-compatible, change this to be a SemVer version number",
                        package.Version.ToString()));
            }

            // we can tell from here what platform(s) the package targets
            // but given this is a simple package we only
            // ever expect one entry here (crash hard otherwise)
            var frameworks = package.GetSupportedFrameworks();
            if (frameworks.Count() > 1) {
                var platforms = frameworks
                    .Aggregate(new StringBuilder(), (sb, f) => sb.Append(f.ToString() + "; "));

                throw new InvalidOperationException(String.Format(
                    "The input package file {0} targets multiple platforms - {1} - and cannot be transformed into a release package.", InputPackageFile, platforms));

            } else if (!frameworks.Any()) {
                throw new InvalidOperationException(String.Format(
                    "The input package file {0} targets no platform and cannot be transformed into a release package.", InputPackageFile));
            }

            // CS - docs say we don't support dependencies. I can't think of any reason allowing this is useful.
            if (package.DependencySets.Any()) {
                throw new InvalidOperationException(String.Format(
                     "The input package file {0} must have no dependencies.", InputPackageFile));
            }

            var targetFramework = frameworks.Single();

            this.Log().Info("Creating release package: {0} => {1}", InputPackageFile, outputFile);

            string tempPath = null;

            using (Utility.WithTempDirectory(out tempPath, null)) {
                var tempDir = new DirectoryInfo(tempPath);

                extractZipWithEscaping(InputPackageFile, tempPath).Wait();

                var specPath = tempDir.GetFiles("*.nuspec").First().FullName;

                this.Log().Info("Removing unnecessary data");
                removeDependenciesFromPackageSpec(specPath);

                if (releaseNotesProcessor != null) {
                    renderReleaseNotesMarkdown(specPath, releaseNotesProcessor);
                }

                addDeltaFilesToContentTypes(tempDir.FullName);

                contentsPostProcessHook?.Invoke(tempPath);

                HelperExe.CreateZipFromDirectory(outputFile, tempPath).Wait();

                ReleasePackageFile = outputFile;
                return ReleasePackageFile;
            }
        }

        static Task extractZipWithEscaping(string zipFilePath, string outFolder)
        {
            return Task.Run(() => {
                using (var za = ZipArchive.Open(zipFilePath))
                using (var reader = za.ExtractAllEntries()) {
                    while (reader.MoveToNextEntry()) {
                        var parts = reader.Entry.Key.Split('\\', '/').Select(x => Uri.UnescapeDataString(x));
                        var decoded = String.Join(Path.DirectorySeparatorChar.ToString(), parts);

                        var fullTargetFile = Path.Combine(outFolder, decoded);
                        var fullTargetDir = Path.GetDirectoryName(fullTargetFile);
                        Directory.CreateDirectory(fullTargetDir);

                        Utility.Retry(() => {
                            if (reader.Entry.IsDirectory) {
                                Directory.CreateDirectory(Path.Combine(outFolder, decoded));
                            } else {
                                reader.WriteEntryToFile(Path.Combine(outFolder, decoded));
                            }
                        }, 5);
                    }
                }
            });
        }

        void renderReleaseNotesMarkdown(string specPath, Func<string, string> releaseNotesProcessor)
        {
            var doc = new XmlDocument();
            doc.Load(specPath);

            // XXX: This code looks full tart
            var metadata = doc.DocumentElement.ChildNodes
                .OfType<XmlElement>()
                .First(x => x.Name.ToLowerInvariant() == "metadata");

            var releaseNotes = metadata.ChildNodes
                .OfType<XmlElement>()
                .FirstOrDefault(x => x.Name.ToLowerInvariant() == "releasenotes");

            if (releaseNotes == null) {
                this.Log().Info("No release notes found in {0}", specPath);
                return;
            }

            releaseNotes.InnerText = String.Format("<![CDATA[\n" + "{0}\n" + "]]>",
                releaseNotesProcessor(releaseNotes.InnerText));

            doc.Save(specPath);
        }

        void removeDependenciesFromPackageSpec(string specPath)
        {
            var xdoc = new XmlDocument();
            xdoc.Load(specPath);

            var metadata = xdoc.DocumentElement.FirstChild;
            var dependenciesNode = metadata.ChildNodes.OfType<XmlElement>().FirstOrDefault(x => x.Name.ToLowerInvariant() == "dependencies");
            if (dependenciesNode != null) {
                metadata.RemoveChild(dependenciesNode);
            }

            xdoc.Save(specPath);
        }

        static internal void addDeltaFilesToContentTypes(string rootDirectory)
        {
            var doc = new XmlDocument();
            var path = Path.Combine(rootDirectory, "[Content_Types].xml");
            doc.Load(path);

            ContentType.Merge(doc);
            ContentType.Clean(doc);

            using (var sw = new StreamWriter(path, false, Encoding.UTF8)) {
                doc.Save(sw);
            }
        }
    }
}