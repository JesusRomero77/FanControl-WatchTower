using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace FanCompanion
{
    /// <summary>
    /// Pestaña de configuración del plugin CurveReader: elegir un perfil de FanControl y, para cada
    /// control y sensor, si se registra en el log, su intervalo y su umbral de cambio. Lee y escribe
    /// CurveReaderLoggerConfig.xml, el archivo que usa el plugin.
    /// </summary>
    public partial class TabCurveReader : UserControl, IConfiguratorTab
    {
        // Puntos que se resta al tamaño de la fuente de letra del desplegable del umbral respecto a la del formulario.
        private const float RowFontReduction = 1F;

        // Nombre del plugin, que es el título de la pestaña. No se traduce.
        private const string TabTitle = "CurveReader";

        // Carpeta de FanControl elegida por el usuario; null mientras no se haya configurado.
        private string _fanControlFolder;

        // Ajustes que el usuario está editando para el perfil seleccionado; null si no hay ninguno.
        private ProfileConfig _profileConfig;

        // Ajustes de cada perfil ya abierto en esta sesión, con el nombre de archivo del perfil como clave.
        // Así, al volver a un perfil, se recuperan los cambios que el usuario hizo y aún no guardó.
        private readonly Dictionary<string, ProfileConfig> _openedProfileConfigs =
            new Dictionary<string, ProfileConfig>(StringComparer.OrdinalIgnoreCase);

        // Perfiles (por nombre de archivo) cuyos ajustes ha cambiado el usuario desde el último guardado.
        // Abrir un perfil sin tocar nada no lo añade, así que no se escribe en el XML.
        private readonly HashSet<string> _changedProfileFileNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Idioma que tiene el XML del log (o el de la app, si el XML no existe o no lo indica). Si el
        // idioma de la app difiere de este, hay un cambio pendiente de guardar.
        private string _savedLanguageName;
        private bool _languageBaselineCaptured;

        /// <summary>
        /// Se lanza cuando la pestaña necesita que el usuario indique la ruta de FanControl y aún no la
        /// hay. MainForm abre el diálogo y después llama a SetFanControlFolder.
        /// </summary>
        public event EventHandler FanControlPathRequested;

        public TabCurveReader()
        {
            InitializeComponent();
        }

        public string Title => TabTitle;

        public bool HasUnsavedChanges => _changedProfileFileNames.Count > 0 || HasLanguageChanged;

        // True si el usuario ha cambiado el idioma de la app desde que se leyó el del XML.
        private bool HasLanguageChanged =>
            _languageBaselineCaptured &&
            !string.Equals(_savedLanguageName, Translator.CurrentLanguageName, StringComparison.OrdinalIgnoreCase);

        // Escribe en cada etiqueta el texto del idioma actual y reconstruye el panel de filas.
        // La primera vez que se llama (al abrir la app, antes de que el usuario toque nada) se anota
        // el idioma que ya tiene el XML, para poder detectar después si el usuario lo cambia.
        public void ApplyLanguage()
        {
            if (!_languageBaselineCaptured) CaptureLanguageBaseline();

            profileLabel.Text = Translator.Translate(TextId.Profile);
            mainToolTip.SetToolTip(refreshProfilesButton, Translator.Translate(TextId.RefreshProfiles));
            BuildProfileConfigPanel();
        }

        // Anota el idioma del XML. Si el XML no existe, está dañado o no indica idioma, se toma el
        // actual, de modo que no cuente como cambio pendiente hasta que el usuario lo cambie.
        private void CaptureLanguageBaseline()
        {
            string error;
            LoggerConfig storedConfig = LoggerConfigFile.Load(out error);
            string storedLanguage = storedConfig?.LanguageName;

            _savedLanguageName = string.IsNullOrEmpty(storedLanguage) ? Translator.CurrentLanguageName : storedLanguage;
            _languageBaselineCaptured = true;
        }

        // MainForm comunica la carpeta de FanControl: se guarda y se cargan los perfiles en el desplegable.
        public void SetFanControlFolder(string fanControlFolder)
        {
            _fanControlFolder = fanControlFolder;
            LoadProfiles();
        }

        // Guarda en CurveReaderLoggerConfig.xml solo lo que ha cambiado: parte de lo que ya hay escrito,
        // sustituye (o añade) los perfiles que el usuario ha modificado, actualiza el idioma y deja el
        // resto como estaba. Sin cambios no toca el archivo, salvo que no exista: entonces lo crea
        // (sin perfiles) para que el plugin lo encuentre con su estructura.
        public bool Save(out string error)
        {
            error = null;
            if (!HasUnsavedChanges && File.Exists(LoggerConfigFile.FilePath)) return true;

            LoggerConfig storedConfig = LoggerConfigFile.Load(out error);
            if (storedConfig == null) return false;

            storedConfig.LanguageName = Translator.CurrentLanguageName;

            // Cada perfil modificado reemplaza en su sitio al guardado con el mismo nombre de archivo,
            // o se añade al final si aún no tenía configuración.
            foreach (string fileName in _changedProfileFileNames)
            {
                ProfileConfig changedConfig = _openedProfileConfigs[fileName];
                ProfileConfig existingConfig = storedConfig.FindProfile(fileName);

                if (existingConfig == null)
                    storedConfig.Profiles.Add(changedConfig);
                else
                    storedConfig.Profiles[storedConfig.Profiles.IndexOf(existingConfig)] = changedConfig;
            }

            if (!LoggerConfigFile.Save(storedConfig, out error)) return false;

            _changedProfileFileNames.Clear();
            _savedLanguageName = Translator.CurrentLanguageName;
            return true;
        }

        // Anota que el perfil que se está editando tiene cambios sin guardar.
        private void MarkProfileChanged()
        {
            if (_profileConfig != null) _changedProfileFileNames.Add(_profileConfig.FileName);
        }

        // Rellena el desplegable con los perfiles de la carpeta que indica el CACHE y deja
        // seleccionado el perfil activo de FanControl.
        private void LoadProfiles()
        {
            profilesComboBox.Items.Clear();
            if (_fanControlFolder == null) return;

            string error;
            ProfileLocation location = ProfileLocator.Locate(_fanControlFolder, out error);
            if (location == null)
            {
                MessageBox.Show(this, "No se ha podido leer el CACHE de FanControl:\r\n" + error,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            foreach (string path in ProfileLocator.GetProfileFiles(location.ProfilesFolder))
            {
                ProfileItem item = new ProfileItem(path);
                int index = profilesComboBox.Items.Add(item);

                if (string.Equals(item.FileName, location.ActiveProfileFileName, StringComparison.OrdinalIgnoreCase))
                    profilesComboBox.SelectedIndex = index;
            }
        }

        // Al elegir un perfil: si ya se abrió antes en esta sesión, se recuperan sus ajustes con los cambios
        // pendientes de guardar. Si no, se lee su contenido, se cruzan sus controles y sensores con los
        // ajustes guardados (o los valores por defecto) y se recuerda el resultado. Después se reconstruye el panel.
        private void ProfilesComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            ProfileItem selected = profilesComboBox.SelectedItem as ProfileItem;
            if (selected == null)
            {
                _profileConfig = null;
                BuildProfileConfigPanel();
                return;
            }

            ProfileConfig openedConfig;
            if (_openedProfileConfigs.TryGetValue(selected.FileName, out openedConfig))
            {
                _profileConfig = openedConfig;
                BuildProfileConfigPanel();
                return;
            }

            string error;
            ProfileContent content = ProfileReader.Read(selected.FilePath, out error);
            if (content == null)
            {
                MessageBox.Show(this, error, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Si el archivo de configuración existe pero no se puede leer, se avisa y se siguen
            // mostrando los valores por defecto.
            LoggerConfig storedConfig = LoggerConfigFile.Load(out error);
            if (storedConfig == null)
            {
                MessageBox.Show(this, error, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                storedConfig = new LoggerConfig();
            }

            _profileConfig = ProfileConfigBuilder.Build(
                selected.FileName, content, storedConfig.FindProfile(selected.FileName));
            _openedProfileConfigs[selected.FileName] = _profileConfig;
            BuildProfileConfigPanel();
        }

        // Botón de recargar junto al desplegable de perfiles. Si ya se eligió la ruta de FanControl,
        // vuelve a leer el CACHE y recarga la lista de perfiles (y el perfil activo). Si aún no se ha
        // elegido, pide a MainForm que la solicite al usuario.
        private void RefreshProfilesButton_Click(object sender, EventArgs e)
        {
            if (_fanControlFolder == null)
            {
                FanControlPathRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            LoadProfiles();
        }

        // Reconstruye el panel desde cero a partir de _profileConfig: una tabla con la sección de
        // controles, la de curvas y la de sensores, en el mismo orden que FanControl.
        private void BuildProfileConfigPanel()
        {
            profileConfigPanel.SuspendLayout();

            // Se eliminan los controles anteriores (al eliminar uno sale solo de la lista).
            while (profileConfigPanel.Controls.Count > 0)
                profileConfigPanel.Controls[0].Dispose();

            if (_profileConfig != null)
            {
                TableLayoutPanel table = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    ColumnCount = 4
                };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // casilla de registro
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));  // nombre
                table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // intervalo
                table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // umbral

                AddSection(table, Translator.Translate(TextId.Controls), _profileConfig.Controls);
                AddCurvesSection(table);
                AddSection(table, Translator.Translate(TextId.Sensors), _profileConfig.Sensors);

                profileConfigPanel.Controls.Add(table);
            }

            profileConfigPanel.ResumeLayout();
        }

        // Añade una sección con filas: la cabecera y una fila por cada control o sensor.
        private void AddSection(TableLayoutPanel table, string title, List<ItemConfig> items)
        {
            AddSectionHeader(table, title);
            foreach (ItemConfig item in items)
                AddItemRow(table, item);
        }

        // Añade la sección de curvas: la misma cabecera que las demás, para dejarla preparada para
        // cuando las curvas se registren, y una nota indicando que de momento no se registran.
        private void AddCurvesSection(TableLayoutPanel table)
        {
            AddSectionHeader(table, Translator.Translate(TextId.Curves));

            Label note = new Label
            {
                Text = Translator.Translate(TextId.CurvesNotRecorded),
                AutoSize = true,
                UseMnemonic = false,
                ForeColor = SystemColors.GrayText
            };
            AddFullWidthRow(table, note);
        }

        // Añade la cabecera de una sección: encabezado de la casilla de registro, título de la sección
        // en negrita, encabezados del intervalo y del umbral, y una línea fina debajo.
        private void AddSectionHeader(TableLayoutPanel table, string title)
        {
            int headerRow = table.RowCount;
            table.RowCount = headerRow + 1;

            Label logHeaderLabel = new Label
            {
                Text = Translator.Translate(TextId.Log),
                AutoSize = true,
                UseMnemonic = false,
                Margin = new Padding(3, 12, 3, 3),
                Anchor = AnchorStyles.Top
            };
            Label titleLabel = new Label
            {
                Text = title,
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                UseMnemonic = false,
                Margin = new Padding(3, 12, 3, 3)
            };
            Label intervalHeaderLabel = new Label
            {
                Text = Translator.Translate(TextId.Interval),
                AutoSize = true,
                UseMnemonic = false,
                Margin = new Padding(3, 12, 3, 3),
                Anchor = AnchorStyles.Top
            };
            Label thresholdHeaderLabel = new Label
            {
                Text = Translator.Translate(TextId.ChangeThreshold),
                AutoSize = true,
                UseMnemonic = false,
                Margin = new Padding(3, 12, 3, 3),
                Anchor = AnchorStyles.Top
            };
            table.Controls.Add(logHeaderLabel, 0, headerRow);
            table.Controls.Add(titleLabel, 1, headerRow);
            table.Controls.Add(intervalHeaderLabel, 2, headerRow);
            table.Controls.Add(thresholdHeaderLabel, 3, headerRow);

            Label line = new Label { BorderStyle = BorderStyle.Fixed3D, AutoSize = false, Height = 2, Dock = DockStyle.Fill };
            AddFullWidthRow(table, line);
        }

        // Añade la fila de un control o sensor: casilla de registro, nombre, intervalo y umbral.
        // Cada cambio del usuario se escribe directamente en 'item' y marca el perfil como modificado.
        private void AddItemRow(TableLayoutPanel table, ItemConfig item)
        {
            int row = table.RowCount;
            table.RowCount = row + 1;

            // La casilla va sola en su columna; Anchor = None la centra bajo el encabezado.
            CheckBox includeCheckBox = new CheckBox
            {
                Checked = item.IncludedInLog,
                AutoSize = true,
                Anchor = AnchorStyles.None
            };

            // UseMnemonic = false para que un "&" en el nombre se muestre tal cual.
            Label nameLabel = new Label
            {
                Text = item.Name,
                AutoSize = true,
                UseMnemonic = false,
                Anchor = AnchorStyles.Left
            };

            NumericUpDown intervalNumeric = CreateIntervalNumericUpDown(item.IntervalSeconds);
            ComboBox thresholdComboBox = CreateValueComboBox(
                Enumerable.Range(ItemConfig.MinThreshold, ItemConfig.MaxThreshold - ItemConfig.MinThreshold + 1),
                item.Threshold);

            // Refleja el estado de la casilla en el resto de la fila: si el elemento no se registra,
            // su nombre y el control del intervalo se sombrean y los dos controles se deshabilitan.
            Action updateRowAppearance = () =>
            {
                bool included = includeCheckBox.Checked;
                nameLabel.BackColor = included ? Color.Transparent : SystemColors.ControlDark;
                intervalNumeric.BackColor = included ? SystemColors.Window : SystemColors.ControlDark;
                intervalNumeric.Enabled = included;
                thresholdComboBox.Enabled = included;
            };
            updateRowAppearance();

            // Los manejadores se asignan después de dar el valor inicial a los controles, para que
            // cargar la fila no cuente como un cambio del usuario.
            includeCheckBox.CheckedChanged += (sender, e) =>
            {
                item.IncludedInLog = includeCheckBox.Checked;
                updateRowAppearance();
                MarkProfileChanged();
            };
            intervalNumeric.ValueChanged += (sender, e) =>
            {
                item.IntervalSeconds = (int)intervalNumeric.Value;
                MarkProfileChanged();
            };
            thresholdComboBox.SelectedIndexChanged += (sender, e) =>
            {
                item.Threshold = (int)thresholdComboBox.SelectedItem;
                MarkProfileChanged();
            };

            // Pulsar sobre el nombre también marca o desmarca la casilla, como si fuera su texto.
            nameLabel.Click += (sender, e) => includeCheckBox.Checked = !includeCheckBox.Checked;

            table.Controls.Add(includeCheckBox, 0, row);
            table.Controls.Add(nameLabel, 1, row);
            table.Controls.Add(intervalNumeric, 2, row);
            table.Controls.Add(thresholdComboBox, 3, row);
        }

        // Añade una fila con un único control que ocupa todo el ancho de la tabla.
        private static void AddFullWidthRow(TableLayoutPanel table, Control control)
        {
            int row = table.RowCount;
            table.RowCount = row + 1;
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, table.ColumnCount);
        }

        // Dibuja cada elemento del desplegable del umbral. Si el desplegable está deshabilitado, el
        // fondo es ControlDark, igual que el del nombre del elemento; si no, se usan los colores
        // normales de Windows, incluido el resaltado al pasar el ratón por la lista.
        private void RowComboBox_DrawItem(object sender, DrawItemEventArgs e)
        {
            ComboBox comboBox = (ComboBox)sender;
            if (e.Index < 0) return;

            bool enabled = comboBox.Enabled;
            bool highlighted = enabled && (e.State & DrawItemState.Selected) != 0;

            Color backColor = !enabled ? SystemColors.ControlDark
                            : highlighted ? SystemColors.Highlight
                            : SystemColors.Window;
            Color textColor = !enabled ? SystemColors.ControlText
                            : highlighted ? SystemColors.HighlightText
                            : SystemColors.WindowText;

            using (SolidBrush brush = new SolidBrush(backColor))
                e.Graphics.FillRectangle(brush, e.Bounds);

            TextRenderer.DrawText(e.Graphics, comboBox.Items[e.Index].ToString(), e.Font, e.Bounds,
                textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            if (enabled) e.DrawFocusRectangle();
        }

        // Crea el control numérico del intervalo, con el mismo estilo de letra que el desplegable:
        // negrita y algo más pequeña que la del formulario.
        private NumericUpDown CreateIntervalNumericUpDown(int value)
        {
            Font numericFont = new Font(Font.FontFamily, Math.Max(Font.Size - RowFontReduction, 6F), FontStyle.Bold);

            // Minimum y Maximum se asignan antes que Value, porque Value debe estar dentro del rango.
            NumericUpDown numeric = new NumericUpDown
            {
                Font = numericFont,
                Minimum = ItemConfig.MinIntervalSeconds,
                Maximum = ItemConfig.MaxIntervalSeconds,
                Value = value,
                Width = 60,
                Anchor = AnchorStyles.None
            };
            numeric.Disposed += (sender, e) => numericFont.Dispose();

            return numeric;
        }

        // Crea un desplegable de fila con los valores indicados y uno seleccionado, con el estilo común:
        // letra en negrita algo más pequeña que la del formulario y fondo ControlDark al deshabilitarse.
        private ComboBox CreateValueComboBox(IEnumerable<int> values, int selectedValue)
        {
            Font comboFont = new Font(Font.FontFamily, Math.Max(Font.Size - RowFontReduction, 6F), FontStyle.Bold);

            ComboBox comboBox = new ComboBox
            {
                Font = comboFont,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = comboFont.Height + 2,
                Width = 60,
                Anchor = AnchorStyles.None
            };
            comboBox.DrawItem += RowComboBox_DrawItem;
            comboBox.Disposed += (sender, e) => comboFont.Dispose();

            foreach (int value in values)
                comboBox.Items.Add(value);
            comboBox.SelectedItem = selectedValue;

            return comboBox;
        }
    }
}