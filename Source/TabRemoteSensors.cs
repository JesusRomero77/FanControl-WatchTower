using System;
using System.IO;
using System.Windows.Forms;
using System.Xml.Linq;

namespace FanCompanion
{
    /// <summary>
    /// Pestaña de configuración del plugin RemoteSensors: nombre y/o IP del PC remoto donde corre
    /// DataServer. Lee y escribe RemoteSensorsConfig.xml, el archivo que usa el plugin.
    /// </summary>
    public partial class TabRemoteSensors : UserControl, IConfiguratorTab
    {
        // Nombre del plugin, que es el título de la pestaña. No se traduce.
        private const string TabTitle = "RemoteSensors";

        // Nombres exactos de los elementos que lee el plugin (XmlSerializer distingue mayúsculas).
        private const string RootElementName = "ClientSettings";
        private const string PcNameElementName = "PcName";
        private const string IpElementName = "Ip";

        // Valores que muestran los campos tras leer o guardar el XML. Con ellos se detecta si el
        // usuario ha cambiado algo. Si el XML tenía una IP que no es una IPv4 válida, el campo la
        // muestra vacía y aquí figura vacía.
        private string _savedPcName = "";
        private string _shownIp = "";
        private bool _settingsLoaded;

        // Esta pestaña nunca necesita pedir la ruta de FanControl (el XML no depende de ella), pero
        // la interfaz obliga a tener el evento.
#pragma warning disable CS0067
        public event EventHandler FanControlPathRequested;
#pragma warning restore CS0067

        public TabRemoteSensors()
        {
            InitializeComponent();
        }

        public string Title => TabTitle;

        // Ruta del XML del plugin, en %LocalAppData%\FanControl.
        private static string ConfigFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FanControl", "RemoteSensorsConfig.xml");

        // Lo que hay ahora escrito en cada campo. El nombre se recorta por los extremos; la IP es
        // "" mientras esté vacía o a medias.
        private string CurrentPcName => pcNameTextBox.Text.Trim();
        private string CurrentIp => ipAddressBox.Value;

        // True si la IP tiene algunos números pero no los cuatro: no se puede guardar.
        private bool HasPartialIp => !ipAddressBox.IsEmpty && !ipAddressBox.IsComplete;

        public bool HasUnsavedChanges =>
            _settingsLoaded && (CurrentPcName != _savedPcName || CurrentIp != _shownIp || HasPartialIp);

        // Escribe en cada etiqueta el texto del idioma actual. La primera vez que se llama (al abrir
        // la app) se leen también los valores que ya tiene el XML y se muestran en los campos.
        public void ApplyLanguage()
        {
            if (!_settingsLoaded) LoadSettings();

            warningLabel.Text = Translator.Translate(TextId.RemoteSensorsWarning);
            pcNameLabel.Text = Translator.Translate(TextId.RemotePcName);
            pcNameHelpLabel.Text = Translator.Translate(TextId.RemotePcNameHelp);
            ipLabel.Text = Translator.Translate(TextId.RemoteIp);
            ipHelpLabel.Text = Translator.Translate(TextId.RemoteIpHelp);
            noteLabel.Text = Translator.Translate(TextId.RemoteSensorsNote);
        }

        // Esta pestaña no usa la carpeta de FanControl.
        public void SetFanControlFolder(string fanControlFolder)
        {
        }

        // Lee PcName e Ip del XML, si existe y tiene el formato esperado, y los muestra. Si el archivo
        // no se puede leer, los campos quedan vacíos; Save avisará del problema antes de escribir nada.
        private void LoadSettings()
        {
            _settingsLoaded = true;
            string savedIp = "";

            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    XElement root = XDocument.Load(ConfigFilePath).Root;
                    if (root != null && root.Name == RootElementName)
                    {
                        _savedPcName = ((string)root.Element(PcNameElementName) ?? "").Trim();
                        savedIp = ((string)root.Element(IpElementName) ?? "").Trim();
                    }
                }
            }
            catch (Exception)
            {
                // XML ilegible: se dejan los valores vacíos.
            }

            pcNameTextBox.Text = _savedPcName;
            ipAddressBox.Value = savedIp;
            _shownIp = ipAddressBox.Value;
        }

        // Guarda PcName e Ip en el XML del plugin. Una IP a medias no se guarda: se devuelve un error.
        // Sin cambios no toca el archivo, salvo que no exista: entonces lo crea con los campos vacíos,
        // igual que lo crea el propio plugin. Si ya existe, se modifica solo el campo que ha cambiado
        // el usuario y se conserva el resto (comentarios y valores que no ha tocado). Si el archivo
        // existe pero no tiene el formato esperado, no se sobrescribe y se devuelve un error.
        public bool Save(out string error)
        {
            error = null;

            if (HasPartialIp)
            {
                error = "La IP del PC remoto está incompleta: escribe los cuatro números o déjala vacía.";
                return false;
            }

            bool fileExists = File.Exists(ConfigFilePath);
            if (!HasUnsavedChanges && fileExists) return true;

            try
            {
                XDocument document = fileExists ? XDocument.Load(ConfigFilePath) : CreateDefaultDocument();
                XElement root = document.Root;

                if (root == null || root.Name != RootElementName)
                {
                    error = "El archivo RemoteSensorsConfig.xml no tiene el formato esperado " +
                            "(falta el elemento " + RootElementName + "). No se ha modificado.";
                    return false;
                }

                // En un archivo nuevo se escriben los dos campos; en uno existente, solo los cambiados.
                if (!fileExists || CurrentPcName != _savedPcName)
                    root.SetElementValue(PcNameElementName, CurrentPcName);
                if (!fileExists || CurrentIp != _shownIp)
                    root.SetElementValue(IpElementName, CurrentIp);

                Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath));
                document.Save(ConfigFilePath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            _savedPcName = CurrentPcName;
            _shownIp = CurrentIp;
            return true;
        }

        // Documento con la misma estructura y los mismos comentarios que crea el propio plugin la
        // primera vez que se carga, con los dos campos vacíos.
        private static XDocument CreateDefaultDocument()
        {
            return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(RootElementName,
                    new XComment(" Name of the REMOTE PC: type 'hostname' in a console on the remote PC. "),
                    new XElement(PcNameElementName, ""),
                    new XComment(" IP of the REMOTE PC: type 'ipconfig' in a console on the remote PC. "),
                    new XElement(IpElementName, "")));
        }
    }
}