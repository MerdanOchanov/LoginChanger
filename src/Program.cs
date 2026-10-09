using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Sql;
using System.Data.Common;
using System.Xml.Linq;
using System.Data.SqlClient;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

// ===== общие помощники =====
static class App
{
    public static string Dir = AppDomain.CurrentDomain.BaseDirectory;
    public static event Action<string> OnLog;

    public static void Log(string s)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + s;
        if (Cfg.LogToFile) try
        {
            string d = Path.Combine(Dir, "logs");
            Directory.CreateDirectory(d);
            File.AppendAllText(Path.Combine(d, DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                DateTime.Now.ToString("yyyy-MM-dd ") + line + "\r\n", Encoding.UTF8);
        }
        catch { }
        if (OnLog != null) OnLog(line);
    }

    static string IniPath { get { return Path.Combine(Dir, "loginchanger.ini"); } }
    public static string TemplatesDir { get { return string.IsNullOrEmpty(Cfg.TplDir) ? Path.Combine(Dir, "templates") : Cfg.TplDir; } }
    // удаляем журналы старше заданного числа дней
    public static void PurgeLogs()
    {
        try
        {
            string d = Path.Combine(Dir, "logs"); if (!Directory.Exists(d)) return;
            foreach (string f in Directory.GetFiles(d, "*.log"))
                if ((DateTime.Now - File.GetLastWriteTime(f)).TotalDays > Cfg.LogDays) File.Delete(f);
        }
        catch { }
    }
    public static Dictionary<string, string> LoadIni()
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (string l in File.ReadAllLines(IniPath, Encoding.UTF8))
            {
                int i = l.IndexOf('=');
                if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1);
            }
        }
        catch { }
        return d;
    }
    public static void SaveIni(Dictionary<string, string> d)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var kv in d) sb.AppendLine(kv.Key + "=" + kv.Value);
            File.WriteAllText(IniPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    public static string Q(string n) { return "[" + n.Replace("]", "]]") + "]"; }
    public static string L(string s) { return "N'" + s.Replace("'", "''") + "'"; }
    public static string S(string s) { return "'" + s.Replace("'", "''") + "'"; }   // строковый литерал (Firebird и др.)

    // 8 символов: легко вводить (без 0/O/1/l/I), есть заглавная, строчные и цифры — проходит политику Windows
    public static string GenPwd(int n)
    {
        string up = "ABCDEFGHJKLMNPQRSTUVWXYZ", lo = "abcdefghjkmnpqrstuvwxyz", di = "23456789";
        var chars = new List<char>();
        chars.Add(up[Rnd(up.Length)]);
        chars.Add(di[Rnd(di.Length)]); chars.Add(di[Rnd(di.Length)]);
        if (Cfg.PwdSymbol) chars.Add("!#$%&*+-=?@^_"[Rnd(13)]);
        while (chars.Count < n) chars.Add(lo[Rnd(lo.Length)]);
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = Rnd(i + 1);
            char t = chars[i]; chars[i] = chars[j]; chars[j] = t;
        }
        return new string(chars.ToArray());
    }    static RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
    static int Rnd(int max)
    {
        byte[] b = new byte[4];
        uint lim = uint.MaxValue - (uint.MaxValue % (uint)max);
        uint v;
        do { rng.GetBytes(b); v = BitConverter.ToUInt32(b, 0); } while (v >= lim);
        return (int)(v % (uint)max);
    }

    public const string UsersSql =
        @"SELECT dp.principal_id AS pid, dp.name AS nm, dp.type AS tp, sp.name AS lg, sp.type AS lt,
 CASE WHEN sp.sid IS NULL AND dp.sid IS NOT NULL
   AND (dp.authentication_type=1 OR (dp.authentication_type=3 AND ISNULL((SELECT containment FROM sys.databases WHERE database_id=DB_ID()),0)=0))
   THEN 1 ELSE 0 END AS orphan,
 CASE WHEN sp.sid=0x01 OR sp.name='sa'
  OR EXISTS(SELECT 1 FROM sys.server_role_members rm JOIN sys.server_principals r ON r.principal_id=rm.role_principal_id AND r.name='sysadmin' WHERE rm.member_principal_id=sp.principal_id)
  OR EXISTS(SELECT 1 FROM sys.database_role_members rm JOIN sys.database_principals r ON r.principal_id=rm.role_principal_id AND r.name='db_owner' WHERE rm.member_principal_id=dp.principal_id)
  THEN 1 ELSE 0 END AS prot,
 ISNULL(sp.is_disabled,0) AS dis, CONVERT(varchar(200),dp.sid,1) AS sidhex
FROM sys.database_principals dp LEFT JOIN sys.server_principals sp ON sp.sid=dp.sid
WHERE dp.type IN ('S','U','G') AND dp.name NOT IN ('dbo','guest','INFORMATION_SCHEMA','sys') ORDER BY dp.name";

    public const string LoginsSql =
        @"SELECT sp.name AS nm, sp.type AS tp, sp.is_disabled AS dis FROM sys.server_principals sp
WHERE sp.type IN ('S','U','G') AND sp.name NOT LIKE '##%' AND sp.name NOT LIKE 'NT %' AND sp.sid<>0x01
 AND NOT EXISTS(SELECT 1 FROM sys.server_role_members rm JOIN sys.server_principals r ON r.principal_id=rm.role_principal_id AND r.name='sysadmin' WHERE rm.member_principal_id=sp.principal_id)
 AND NOT EXISTS (SELECT 1 FROM sys.database_principals d WHERE d.sid=sp.sid) ORDER BY sp.name";
}

static class UI
{
    public const string T = "LoginChanger";
    // тестовый режим: ответы без окон (UI.Auto = true/false), сообщения копятся в Msgs
    public static bool? Auto;
    public static List<string> Msgs = new List<string>();
    public static bool Ask(string t) { if (Auto.HasValue) { Msgs.Add("ASK: " + t); return Auto.Value; } return MessageBox.Show(t, T, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; }
    public static void Warn(string t) { if (Auto.HasValue) { Msgs.Add("WARN: " + t); return; } MessageBox.Show(t, T, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    public static void Info(string t) { if (Auto.HasValue) { Msgs.Add("INFO: " + t); return; } MessageBox.Show(t, T, MessageBoxButtons.OK, MessageBoxIcon.Information); }

    public static int AutoChoice; public static string AutoPick;

    // несколько вариантов кнопками; -1 = отмена
    public static int Choose(string title, string note, string[] options)
    {
        if (Auto.HasValue) { Msgs.Add("CHOOSE: " + note); return AutoChoice; }
        using (var f = new Form())
        {
            f.Text = title; f.StartPosition = FormStartPosition.CenterParent; f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false; f.AutoScaleMode = AutoScaleMode.Dpi; f.Font = new Font("Segoe UI", 10f);
            var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20), AutoSize = true, MinimumSize = new Size(500, 0) };
            tl.Controls.Add(new Label { Text = note, AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(3, 0, 3, 14) });
            int res = -1;
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                var b = new Button { Text = options[i], AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4), Tag = i == 0 ? "primary" : null };
                b.Click += delegate { res = idx; f.Close(); };
                tl.Controls.Add(b);
            }
            var cn = new Button { Text = "Отмена", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 12, 3, 0), DialogResult = DialogResult.Cancel };
            tl.Controls.Add(cn);
            f.Controls.Add(tl); f.AutoSize = true; f.AutoSizeMode = AutoSizeMode.GrowAndShrink; f.CancelButton = cn;
            Theme.Apply(f);
            f.ShowDialog();
            return res;
        }
    }

    // выбор одного значения из списка; null = отмена
    public static string Pick(string title, string label, List<string> items)
    {
        if (Auto.HasValue) { Msgs.Add("PICK: " + label); return AutoPick; }
        using (var f = new Form())
        {
            f.Text = title; f.StartPosition = FormStartPosition.CenterParent; f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false; f.AutoScaleMode = AutoScaleMode.Dpi; f.Font = new Font("Segoe UI", 10f);
            var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(20), AutoSize = true, MinimumSize = new Size(480, 0) };
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 14) };
            foreach (string s in items) cb.Items.Add(s);
            if (cb.Items.Count > 0) cb.SelectedIndex = 0;
            var ok = new Button { Text = "Выбрать", DialogResult = DialogResult.OK, AutoSize = true, Tag = "primary" };
            var cn = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
            var fl = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            fl.Controls.Add(cn); fl.Controls.Add(ok);
            tl.Controls.Add(new Label { Text = label, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 0, 3, 8) });
            tl.Controls.Add(cb); tl.Controls.Add(fl);
            f.Controls.Add(tl); f.AutoSize = true; f.AutoSizeMode = AutoSizeMode.GrowAndShrink; f.AcceptButton = ok; f.CancelButton = cn;
            Theme.Apply(f);
            return f.ShowDialog() == DialogResult.OK ? (string)cb.SelectedItem : null;
        }
    }

    public static string Prompt(string title, string label, string def)
    {
        using (var f = new Form())
        {
            f.Text = title; f.StartPosition = FormStartPosition.CenterParent; f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false; f.AutoScaleMode = AutoScaleMode.Dpi; f.Font = new Font("Segoe UI", 10f);
            var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(20), AutoSize = true };
            var tb = new TextBox { Text = def, Width = 300, Dock = DockStyle.Fill };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
            var cn = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
            var fl = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            fl.Controls.Add(cn); fl.Controls.Add(ok);
            tl.Controls.Add(new Label { Text = label, AutoSize = true }, 0, 0);
            tl.Controls.Add(tb, 0, 1); tl.Controls.Add(fl, 0, 2);
            f.Controls.Add(tl); f.AutoSize = true; f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.AcceptButton = ok; f.CancelButton = cn; ok.Tag = "primary"; Theme.Apply(f);
            return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
        }
    }
}

// ===== работа с БД =====
class Db
{
    public DbConnection C;
    public DbTransaction Tx;
    public string Server, Name, Who;
    public string Kind = "mssql";      // mssql | firebird | mysql
    public Dialect D = new MssqlDialect();
    public Db() { D.Bind(this); }

    DbCommand Cmd(string sql, object[] p)
    {
        var c = C.CreateCommand();
        c.CommandText = sql; c.CommandTimeout = Cfg.CmdTimeout; c.Transaction = Tx;
        for (int i = 0; p != null && i + 1 < p.Length; i += 2)
        {
            var q = c.CreateParameter(); q.ParameterName = (string)p[i]; q.Value = p[i + 1] ?? DBNull.Value; c.Parameters.Add(q);
        }
        return c;
    }
    // читаем строки вручную: DataTable.Load падает на ограничениях, которые сообщают некоторые ODBC-драйверы
    static DataTable Read(IDataReader r)
    {
        var t = new DataTable();
        for (int i = 0; i < r.FieldCount; i++) t.Columns.Add(r.GetName(i), typeof(object));
        var vals = new object[r.FieldCount];
        while (r.Read()) { r.GetValues(vals); t.Rows.Add(vals); }
        return t;
    }
    public DataTable Query(string sql, params object[] p)
    {
        using (var c = Cmd(sql, p)) using (var r = c.ExecuteReader()) return Read(r);
    }
    public int Count(string sql, params object[] p)
    {
        using (var c = Cmd(sql, p))
        {
            object o = c.ExecuteScalar();
            return (o == null || o == DBNull.Value) ? 0 : Convert.ToInt32(o);
        }
    }
    public void Exec(string sql, string logText)
    {
        App.Log("SQL[" + Name + "]: " + (logText ?? sql));
        using (var c = Cmd(sql, null)) c.ExecuteNonQuery();
    }
    // права sysadmin / db_owner без функций IS_*ROLEMEMBER (они падают на «мёртвых» учётках Windows, код 0x534)
    public bool IsSysadmin(string login)
    {
        if (Count("SELECT COUNT(*) FROM sys.server_role_members rm JOIN sys.server_principals r ON r.principal_id=rm.role_principal_id AND r.name='sysadmin' JOIN sys.server_principals m ON m.principal_id=rm.member_principal_id WHERE m.name=@n", "@n", login) > 0) return true;
        if (login.IndexOf((char)92) >= 0)   // Windows: членство через группы; ошибку разрешения имени не считаем админом
        {
            try { return Count("SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin',@n)=1 THEN 1 ELSE 0 END", "@n", login) == 1; }
            catch { return false; }
        }
        return false;
    }
    public bool IsDbOwner(string user)
    {
        return Count("SELECT COUNT(*) FROM sys.database_role_members rm JOIN sys.database_principals r ON r.principal_id=rm.role_principal_id AND r.name='db_owner' JOIN sys.database_principals m ON m.principal_id=rm.member_principal_id WHERE m.name=@n", "@n", user) > 0;
    }
    public DataTable Users() { return D.Users(); }
    public DataTable Logins() { return D.Logins(); }

    public void InTx(Action a)
    {
        Tx = C.BeginTransaction();
        try { a(); Tx.Commit(); }
        catch { try { Tx.Rollback(); } catch { } App.Log("Ошибка, транзакция отменена"); throw; }
        finally { Tx = null; }
    }

    // выполнить пачку команд; inv — обратные команды (того же размера). Если СУБД не умеет откатывать DDL (MySQL),
    // при ошибке уже выполненные команды отменяются обратными.
    public void Batch(List<string> stm, List<string> inv)
    {
        if (D.Transactional) { InTx(delegate { foreach (string s in stm) Exec(s, null); }); return; }
        int done = 0;
        try { foreach (string s in stm) { Exec(s, null); done++; } }
        catch
        {
            if (inv != null) for (int i = done - 1; i >= 0; i--) { try { Exec(inv[i], null); } catch { } }
            App.Log("Ошибка: выполненные команды отменены обратными");
            throw;
        }
    }
}
// ===== СУБД: диалекты (SQL Server, Firebird, MySQL) =====
// Нормализованные таблицы, которые читают матрица и список пользователей:
//   Objects: oid, sch, nm, tp   (tp: V — представление, U — таблица, P — процедура, FN — функция)
//   Users:   pid, nm, tp, lg, lt, orphan, prot, dis, sidhex
//   Logins:  nm, tp, dis        (логины сервера без пользователя в БД; только SQL Server)
//   Perms:   gid, oid, pn, st   (pn: SELECT / EXECUTE; st: G — выдано, D — запрещено)
abstract class Dialect
{
    protected Db db;
    public void Bind(Db d) { db = d; }
    public abstract string Title { get; }
    public virtual bool Transactional { get { return true; } }
    public abstract string Q(string id);
    public virtual string UserRef(string user) { return Q(user); }
    public virtual string Canon(string name) { return name.Trim(); }
    public abstract DataTable Objects(string[] types);
    public abstract DataTable Users();
    public virtual DataTable Logins() { return Tbl("nm", "tp", "dis"); }
    public abstract DataTable Perms(DataTable objs, DataTable users);
    public abstract string Grant(string sch, string nm, string tp, string perm, string user);
    public abstract string Revoke(string sch, string nm, string tp, string perm, string user);
    public virtual string Deny(string sch, string nm, string tp, string perm, string user) { return null; }
    public abstract bool UserExists(string name);
    public abstract bool IsProtected(string name);
    public abstract void CreateUser(string name, string pwd);
    public abstract void DropUser(string name);
    public abstract void ChangePassword(string name, string pwd);
    // описания объектов
    public virtual bool DescLocal(string tp) { return false; }
    public abstract void SetDescription(string sch, string nm, string tp, string text, string src);
    public abstract string DescScript(string sch, string nm, string tp, string text);
    public abstract DataTable Meta(DataTable objs, int maxDef);      // oid, cols, def — для ИИ
    protected static DataTable MetaTbl() { return Tbl("oid", "cols", "def"); }
    protected static string Join40(List<string> l) { if (l == null) return ""; return string.Join(", ", (l.Count > 40 ? l.GetRange(0, 40) : l).ToArray()); }

    protected static DataTable Tbl(params string[] cols)
    {
        var t = new DataTable();
        foreach (string c in cols) t.Columns.Add(c, typeof(object));
        return t;
    }
    protected static string S(object v)
    {
        if (v == null || v is DBNull) return "";
        byte[] b = v as byte[];
        return (b != null ? System.Text.Encoding.UTF8.GetString(b) : Convert.ToString(v)).Trim();
    }
    protected static bool B(object v)
    {
        if (v == null || v is DBNull) return false;
        if (v is bool) return (bool)v;
        string s = Convert.ToString(v).Trim().ToUpper();
        return s == "1" || s == "-1" || s == "TRUE" || s == "T" || s == "Y" || s == "YES";
    }
    protected static bool Wants(string[] types, string tp) { return Array.IndexOf(types, tp) >= 0; }
}

class MssqlDialect : Dialect
{
    public override string Title { get { return "SQL Server"; } }
    public override string Q(string id) { return App.Q(id); }

    public override DataTable Objects(string[] types)
    {
        var tl = new List<string>(); foreach (string t in types) tl.Add("'" + t + "'");
        return db.Query("SELECT o.object_id AS oid, s.name AS sch, o.name AS nm, RTRIM(o.type) AS tp, CONVERT(nvarchar(2000), ep.value) AS descr, CONVERT(nvarchar(200), ep2.value) AS src FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id LEFT JOIN sys.extended_properties ep ON ep.class=1 AND ep.minor_id=0 AND ep.major_id=o.object_id AND ep.name=N'MS_Description' LEFT JOIN sys.extended_properties ep2 ON ep2.class=1 AND ep2.minor_id=0 AND ep2.major_id=o.object_id AND ep2.name=N'LC_Source' WHERE o.is_ms_shipped=0 AND o.type IN (" + string.Join(",", tl.ToArray()) + ") ORDER BY s.name, o.name");
    }
    public override DataTable Users()
    {
        DataTable t = db.Query(App.UsersSql);
        foreach (DataRow r in t.Rows)
        {
            string lt = r["lt"] as string, lg = r["lg"] as string;
            if (Convert.ToInt32(r["prot"]) == 0 && lg != null && (lt == "U" || lt == "G") && db.IsSysadmin(lg)) r["prot"] = 1;
        }
        return t;
    }
    public override DataTable Logins()
    {
        DataTable t = db.Query(App.LoginsSql);
        for (int i = t.Rows.Count - 1; i >= 0; i--)
        {
            string tp = t.Rows[i]["tp"] as string;
            if ((tp == "U" || tp == "G") && db.IsSysadmin((string)t.Rows[i]["nm"])) t.Rows.RemoveAt(i);
        }
        return t;
    }
    public override DataTable Perms(DataTable objs, DataTable users)
    {
        return db.Query("SELECT dp.grantee_principal_id AS gid, dp.major_id AS oid, dp.permission_name AS pn, dp.state AS st FROM sys.database_permissions dp WHERE dp.class=1 AND dp.minor_id=0 AND dp.permission_name IN ('SELECT','EXECUTE','INSERT','UPDATE','DELETE','REFERENCES')");
    }
    string Obj(string sch, string nm) { return "OBJECT::" + App.Q(sch) + "." + App.Q(nm); }
    public override string Grant(string sch, string nm, string tp, string perm, string user) { return "GRANT " + perm + " ON " + Obj(sch, nm) + " TO " + App.Q(user); }
    public override string Revoke(string sch, string nm, string tp, string perm, string user) { return "REVOKE " + perm + " ON " + Obj(sch, nm) + " FROM " + App.Q(user); }
    public override string Deny(string sch, string nm, string tp, string perm, string user) { return "DENY " + perm + " ON " + Obj(sch, nm) + " TO " + App.Q(user); }
    public override bool UserExists(string name) { return db.Count("SELECT COUNT(*) FROM sys.database_principals WHERE name=@n AND type IN ('S','U','G')", "@n", name) > 0; }
    static string Lvl(string tp) { switch (tp) { case "V": return "VIEW"; case "U": return "TABLE"; case "P": case "PC": return "PROCEDURE"; default: return "FUNCTION"; } }
    void Prop(string sch, string nm, string tp, string name, string val)
    {
        string full = App.Q(sch) + "." + App.Q(nm);
        bool ex = db.Count("SELECT COUNT(*) FROM sys.extended_properties WHERE class=1 AND minor_id=0 AND major_id=OBJECT_ID(@o) AND name=@n", "@o", full, "@n", name) > 0;
        string args = ", @level0type=N'SCHEMA', @level0name=" + App.L(sch) + ", @level1type=N'" + Lvl(tp) + "', @level1name=" + App.L(nm);
        if (val.Length == 0) { if (ex) db.Exec("EXEC sys.sp_dropextendedproperty @name=" + App.L(name) + args, null); return; }
        if (val.Length > 3750) val = val.Substring(0, 3750);
        db.Exec("EXEC sys." + (ex ? "sp_updateextendedproperty" : "sp_addextendedproperty") + " @name=" + App.L(name) + ", @value=" + App.L(val) + args, null);
    }
    public override void SetDescription(string sch, string nm, string tp, string text, string src) { Prop(sch, nm, tp, "MS_Description", text); Prop(sch, nm, tp, "LC_Source", src ?? ""); }
    public override string DescScript(string sch, string nm, string tp, string text)
    {
        if (text.Length == 0) return "-- пустое описание для " + sch + "." + nm + " пропущено";
        string full = App.L(App.Q(sch) + "." + App.Q(nm));
        string args = ", @level0type=N'SCHEMA', @level0name=" + App.L(sch) + ", @level1type=N'" + Lvl(tp) + "', @level1name=" + App.L(nm);
        return "IF OBJECT_ID(" + full + ") IS NOT NULL\nBEGIN\n  IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class=1 AND minor_id=0 AND major_id=OBJECT_ID(" + full + ") AND name=N'MS_Description')\n    EXEC sys.sp_updateextendedproperty @name=N'MS_Description', @value=" + App.L(text) + args + ";\n  ELSE\n    EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=" + App.L(text) + args + ";\nEND\nGO";
    }
    public override DataTable Meta(DataTable objs, int maxDef)
    {
        var def = new Dictionary<int, string>(); var cols = new Dictionary<int, List<string>>();
        if (maxDef > 0)
            foreach (DataRow r in db.Query("SELECT m.object_id AS oid, LEFT(m.definition, " + maxDef + ") AS def FROM sys.sql_modules m JOIN sys.objects o ON o.object_id=m.object_id WHERE o.is_ms_shipped=0").Rows)
                def[Convert.ToInt32(r["oid"])] = Convert.ToString(r["def"]);
        foreach (DataRow r in db.Query("SELECT c.object_id AS oid, c.name AS nm, ty.name AS tn FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE o.is_ms_shipped=0 AND o.type IN ('V','U') ORDER BY c.object_id, c.column_id").Rows)
        {
            int k = Convert.ToInt32(r["oid"]); List<string> l; if (!cols.TryGetValue(k, out l)) cols[k] = l = new List<string>();
            l.Add(Convert.ToString(r["nm"]) + " " + Convert.ToString(r["tn"]));
        }
        DataTable t = MetaTbl();
        foreach (DataRow r in objs.Rows) { int k = Convert.ToInt32(r["oid"]); string d; List<string> l; def.TryGetValue(k, out d); cols.TryGetValue(k, out l); t.Rows.Add(k, Join40(l), d ?? ""); }
        return t;
    }

    public override bool IsProtected(string name)
    {
        string l = name.ToLower();
        return l == "sa" || l == "dbo" || l == "guest" || l == "public" || db.IsSysadmin(name) || db.IsDbOwner(name);
    }
    public override void CreateUser(string name, string pwd) { throw new NotSupportedException(); }
    public override void DropUser(string name) { db.Exec("DROP USER " + App.Q(name), null); }
    public override void ChangePassword(string name, string pwd) { db.Exec("ALTER LOGIN " + App.Q(name) + " WITH PASSWORD=" + App.L(pwd), "ALTER LOGIN " + App.Q(name) + " WITH PASSWORD='***'"); }
}

// Firebird 3+ (через ODBC). Пользователи общие для сервера (SEC$USERS), права — на объекты текущей базы.
class FirebirdDialect : Dialect
{
    public override string Title { get { return "Firebird"; } }
    public override string Q(string id) { return "\"" + id.Replace("\"", "\"\"") + "\""; }
    static bool Simple(string s) { return System.Text.RegularExpressions.Regex.IsMatch(s, "^[A-Za-z][A-Za-z0-9_$]*$"); }
    public override string Canon(string name) { name = name.Trim(); return Simple(name) ? name.ToUpper() : name; }
    string Ident(string name) { name = Canon(name); return Q(name); }

    public override DataTable Objects(string[] types)
    {
        DataTable src = db.Query(@"SELECT TRIM(RDB$RELATION_NAME) AS nm, CASE WHEN RDB$VIEW_BLR IS NULL THEN 'U' ELSE 'V' END AS tp, CAST(RDB$DESCRIPTION AS VARCHAR(1000)) AS d FROM RDB$RELATIONS WHERE COALESCE(RDB$SYSTEM_FLAG,0)=0
UNION ALL SELECT TRIM(RDB$PROCEDURE_NAME), 'P', CAST(RDB$DESCRIPTION AS VARCHAR(1000)) FROM RDB$PROCEDURES WHERE COALESCE(RDB$SYSTEM_FLAG,0)=0 AND RDB$PACKAGE_NAME IS NULL
UNION ALL SELECT TRIM(RDB$FUNCTION_NAME), 'FN', CAST(RDB$DESCRIPTION AS VARCHAR(1000)) FROM RDB$FUNCTIONS WHERE COALESCE(RDB$SYSTEM_FLAG,0)=0 AND RDB$PACKAGE_NAME IS NULL");
        var rows = new List<string[]>();
        foreach (DataRow r in src.Rows) { string tp = S(r["tp"]); if (Wants(types, tp)) rows.Add(new[] { S(r["nm"]), tp, S(r["d"]) }); }
        rows.Sort(delegate(string[] a, string[] b) { return string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase); });
        DataTable t = Tbl("oid", "sch", "nm", "tp", "descr"); int id = 0;
        foreach (string[] r in rows) t.Rows.Add(++id, "", r[0], r[1], r[2]);
        return t;
    }
    public override DataTable Users()
    {
        DataTable src = db.Query("SELECT TRIM(SEC$USER_NAME) AS nm, SEC$ADMIN AS adm, SEC$ACTIVE AS act FROM SEC$USERS ORDER BY 1");
        DataTable t = Tbl("pid", "nm", "tp", "lg", "lt", "orphan", "prot", "dis", "sidhex"); int id = 0;
        foreach (DataRow r in src.Rows)
        {
            string nm = S(r["nm"]);
            bool prot = B(r["adm"]) || nm.ToUpper() == "SYSDBA";
            t.Rows.Add(++id, nm, "S", nm, "S", 0, prot ? 1 : 0, B(r["act"]) ? 0 : 1, null);
        }
        return t;
    }
    public override DataTable Perms(DataTable objs, DataTable users)
    {
        var rel = new Dictionary<string, int>(); var pro = new Dictionary<string, int>(); var fn = new Dictionary<string, int>();
        foreach (DataRow r in objs.Rows)
        {
            string tp = S(r["tp"]), n = S(r["nm"]); int oid = Convert.ToInt32(r["oid"]);
            if (tp == "V" || tp == "U") rel[n] = oid; else if (tp == "P") pro[n] = oid; else if (tp == "FN") fn[n] = oid;
        }
        var uid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow r in users.Rows) uid[S(r["nm"])] = Convert.ToInt32(r["pid"]);
        DataTable p = db.Query("SELECT TRIM(RDB$USER) AS u, TRIM(RDB$RELATION_NAME) AS r, RDB$OBJECT_TYPE AS t, RDB$PRIVILEGE AS p FROM RDB$USER_PRIVILEGES WHERE RDB$USER_TYPE=8 AND RDB$PRIVILEGE IN ('S','I','U','D','R','X') AND RDB$FIELD_NAME IS NULL");
        DataTable t = Tbl("gid", "oid", "pn", "st");
        foreach (DataRow r in p.Rows)
        {
            int g, o; string u = S(r["u"]), n = S(r["r"]), priv = S(r["p"]); int ot = Convert.ToInt32(r["t"]);
            if (!uid.TryGetValue(u, out g)) continue;
            if (priv != "X" && ot == 0 && rel.TryGetValue(n, out o)) t.Rows.Add(g, o, priv == "S" ? "SELECT" : priv == "I" ? "INSERT" : priv == "U" ? "UPDATE" : priv == "D" ? "DELETE" : "REFERENCES", "G");
            else if (priv == "X" && ot == 5 && pro.TryGetValue(n, out o)) t.Rows.Add(g, o, "EXECUTE", "G");
            else if (priv == "X" && ot == 15 && fn.TryGetValue(n, out o)) t.Rows.Add(g, o, "EXECUTE", "G");
        }
        return t;
    }
    string Target(string nm, string tp, string perm)
    {
        if (tp == "P") return "PROCEDURE " + Q(nm);
        if (tp == "FN") return "FUNCTION " + Q(nm);
        return Q(nm);
    }
    public override string Grant(string sch, string nm, string tp, string perm, string user) { return "GRANT " + perm + " ON " + Target(nm, tp, perm) + " TO " + Ident(user); }
    public override string Revoke(string sch, string nm, string tp, string perm, string user) { return "REVOKE " + perm + " ON " + Target(nm, tp, perm) + " FROM " + Ident(user); }
    public override bool UserExists(string name)
    {
        return db.Query("SELECT 1 FROM SEC$USERS WHERE SEC$USER_NAME=" + App.S(Canon(name))).Rows.Count > 0;
    }
    static string Kind(string tp) { return tp == "V" ? "VIEW" : tp == "U" ? "TABLE" : tp == "P" ? "PROCEDURE" : "FUNCTION"; }
    public override void SetDescription(string sch, string nm, string tp, string text, string src)
    {
        db.Exec("COMMENT ON " + Kind(tp) + " " + Q(nm) + " IS " + (text.Length == 0 ? "NULL" : App.S(text)), null);
    }
    public override string DescScript(string sch, string nm, string tp, string text)
    {
        return "COMMENT ON " + Kind(tp) + " " + Q(nm) + " IS " + (text.Length == 0 ? "NULL" : App.S(text)) + ";";
    }
    public override DataTable Meta(DataTable objs, int maxDef)
    {
        int md = Math.Min(maxDef, 4000);
        var cols = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); var def = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow r in db.Query("SELECT TRIM(RDB$RELATION_NAME) AS r, TRIM(RDB$FIELD_NAME) AS f FROM RDB$RELATION_FIELDS ORDER BY 1, RDB$FIELD_POSITION").Rows)
        { string k = S(r["r"]); List<string> l; if (!cols.TryGetValue(k, out l)) cols[k] = l = new List<string>(); l.Add(S(r["f"])); }
        if (md > 0)
        {
            foreach (DataRow r in db.Query("SELECT TRIM(RDB$RELATION_NAME) AS n, CAST(RDB$VIEW_SOURCE AS VARCHAR(" + md + ")) AS d FROM RDB$RELATIONS WHERE RDB$VIEW_SOURCE IS NOT NULL").Rows) def["V:" + S(r["n"])] = S(r["d"]);
            foreach (DataRow r in db.Query("SELECT TRIM(RDB$PROCEDURE_NAME) AS n, CAST(RDB$PROCEDURE_SOURCE AS VARCHAR(" + md + ")) AS d FROM RDB$PROCEDURES WHERE RDB$PROCEDURE_SOURCE IS NOT NULL").Rows) def["P:" + S(r["n"])] = S(r["d"]);
        }
        DataTable t = MetaTbl();
        foreach (DataRow r in objs.Rows)
        {
            string n = S(r["nm"]), tp = S(r["tp"]); string d; List<string> l; def.TryGetValue(tp + ":" + n, out d); cols.TryGetValue(n, out l);
            t.Rows.Add(Convert.ToInt32(r["oid"]), Join40(l), d ?? "");
        }
        return t;
    }

    public override bool IsProtected(string name)
    {
        string n = Canon(name);
        if (n.ToUpper() == "SYSDBA") return true;
        DataTable t = db.Query("SELECT SEC$ADMIN AS adm FROM SEC$USERS WHERE SEC$USER_NAME=" + App.S(n));
        return t.Rows.Count > 0 && B(t.Rows[0]["adm"]);
    }
    public override void CreateUser(string name, string pwd) { db.Exec("CREATE USER " + Ident(name) + " PASSWORD " + App.S(pwd), "CREATE USER " + Ident(name) + " PASSWORD '***'"); }
    public override void DropUser(string name) { db.Exec("DROP USER " + Ident(name), null); }
    public override void ChangePassword(string name, string pwd) { db.Exec("ALTER USER " + Ident(name) + " PASSWORD " + App.S(pwd), "ALTER USER " + Ident(name) + " PASSWORD '***'"); }
}

// MySQL / MariaDB (через ODBC). Учётные записи — 'имя'@'хост' (в списке «имя@хост»), права — на объекты выбранной базы.
// DDL в MySQL фиксируется сразу (откат транзакцией невозможен), поэтому при ошибке выполняются обратные команды.
class MySqlDialect : Dialect
{
    public override string Title { get { return "MySQL"; } }
    public override bool Transactional { get { return false; } }
    public override string Q(string id) { return "`" + id.Replace("`", "``") + "`"; }
    static string Lit(string s) { return "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'"; }
    static void Split(string name, out string user, out string host)
    {
        int i = name.LastIndexOf('@');
        if (i > 0) { user = name.Substring(0, i); host = name.Substring(i + 1); } else { user = name; host = "%"; }
        if (host.Length == 0) host = "%";
    }
    public override string Canon(string name) { string u, h; Split(name.Trim(), out u, out h); return u + "@" + h; }
    public override string UserRef(string user) { string u, h; Split(user, out u, out h); return Lit(u) + "@" + Lit(h); }

    public override bool DescLocal(string tp) { return tp == "V"; }
    public override DataTable Objects(string[] types)
    {
        DataTable t = ObjectsRaw(types);
        var sc = Sidecar.Load(db);
        foreach (DataRow r in t.Rows) { string v; if (Convert.ToString(r["tp"]) == "V" && sc.TryGetValue(Convert.ToString(r["nm"]), out v)) r["descr"] = v; }
        return t;
    }
    public override void SetDescription(string sch, string nm, string tp, string text, string src)
    {
        if (tp == "V") { Sidecar.Set(db, nm, text); return; }
        if (text.Length > 1000) text = text.Substring(0, 1000);
        if (tp == "U") db.Exec("ALTER TABLE " + Q(db.Name) + "." + Q(nm) + " COMMENT = " + Lit(text), null);
        else db.Exec("ALTER " + (tp == "P" ? "PROCEDURE " : "FUNCTION ") + Q(db.Name) + "." + Q(nm) + " COMMENT " + Lit(text), null);
    }
    public override string DescScript(string sch, string nm, string tp, string text)
    {
        if (tp == "V") return "-- представление " + nm + ": у MySQL нет комментариев к представлениям, описание хранится в файле программы";
        if (tp == "U") return "ALTER TABLE " + Q(db.Name) + "." + Q(nm) + " COMMENT = " + Lit(text) + ";";
        return "ALTER " + (tp == "P" ? "PROCEDURE " : "FUNCTION ") + Q(db.Name) + "." + Q(nm) + " COMMENT " + Lit(text) + ";";
    }
    public override DataTable Meta(DataTable objs, int maxDef)
    {
        string d = Lit(db.Name);
        var cols = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); var def = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow r in db.Query("SELECT TABLE_NAME AS t, COLUMN_NAME AS c, COLUMN_TYPE AS ty FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=" + d + " ORDER BY TABLE_NAME, ORDINAL_POSITION").Rows)
        { string k = S(r["t"]); List<string> l; if (!cols.TryGetValue(k, out l)) cols[k] = l = new List<string>(); l.Add(S(r["c"]) + " " + S(r["ty"])); }
        if (maxDef > 0)
        {
            foreach (DataRow r in db.Query("SELECT TABLE_NAME AS n, LEFT(VIEW_DEFINITION, " + maxDef + ") AS d FROM information_schema.VIEWS WHERE TABLE_SCHEMA=" + d).Rows) def["V:" + S(r["n"])] = S(r["d"]);
            foreach (DataRow r in db.Query("SELECT ROUTINE_NAME AS n, ROUTINE_TYPE AS ty, LEFT(ROUTINE_DEFINITION, " + maxDef + ") AS d FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=" + d).Rows) def[(S(r["ty"]).ToUpper() == "PROCEDURE" ? "P:" : "FN:") + S(r["n"])] = S(r["d"]);
        }
        DataTable t = MetaTbl();
        foreach (DataRow r in objs.Rows)
        {
            string n = S(r["nm"]), tp = S(r["tp"]); string dd; List<string> l; def.TryGetValue(tp + ":" + n, out dd); cols.TryGetValue(n, out l);
            t.Rows.Add(Convert.ToInt32(r["oid"]), Join40(l), dd ?? "");
        }
        return t;
    }
    DataTable ObjectsRaw(string[] types)
    {
        string d = Lit(db.Name);
        DataTable src = db.Query("SELECT TABLE_NAME AS nm, CASE WHEN TABLE_TYPE='VIEW' THEN 'V' ELSE 'U' END AS tp, CASE WHEN TABLE_COMMENT='VIEW' THEN '' ELSE TABLE_COMMENT END AS d FROM information_schema.TABLES WHERE TABLE_SCHEMA=" + d + " AND TABLE_TYPE IN ('BASE TABLE','VIEW') " +
            "UNION ALL SELECT ROUTINE_NAME, CASE WHEN ROUTINE_TYPE='PROCEDURE' THEN 'P' ELSE 'FN' END, ROUTINE_COMMENT FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=" + d);
        var rows = new List<string[]>();
        foreach (DataRow r in src.Rows) { string tp = S(r["tp"]); if (Wants(types, tp)) rows.Add(new[] { S(r["nm"]), tp, S(r["d"]) }); }
        rows.Sort(delegate(string[] a, string[] b) { return string.Compare(a[0], b[0], StringComparison.OrdinalIgnoreCase); });
        DataTable t = Tbl("oid", "sch", "nm", "tp", "descr"); int id = 0;
        foreach (string[] r in rows) t.Rows.Add(++id, "", r[0], r[1], r[2]);
        return t;
    }
    static readonly string[] SysUsers = { "root", "mysql.sys", "mysql.session", "mysql.infoschema", "mariadb.sys", "debian-sys-maint" };
    public override DataTable Users()
    {
        DataTable src = db.Query("SELECT User AS u, Host AS h, Super_priv AS sp, Grant_priv AS gp, account_locked AS al FROM mysql.user WHERE User<>'' ORDER BY User, Host");
        DataTable t = Tbl("pid", "nm", "tp", "lg", "lt", "orphan", "prot", "dis", "sidhex"); int id = 0;
        foreach (DataRow r in src.Rows)
        {
            string u = S(r["u"]), h = S(r["h"]), nm = u + "@" + h;
            bool prot = Array.IndexOf(SysUsers, u.ToLower()) >= 0 || B(r["sp"]) || B(r["gp"]);
            t.Rows.Add(++id, nm, "S", nm, "S", 0, prot ? 1 : 0, B(r["al"]) ? 1 : 0, null);
        }
        return t;
    }
    public override DataTable Perms(DataTable objs, DataTable users)
    {
        var rel = new Dictionary<string, int>(); var pro = new Dictionary<string, int>(); var fn = new Dictionary<string, int>();
        foreach (DataRow r in objs.Rows)
        {
            string tp = S(r["tp"]), n = S(r["nm"]); int oid = Convert.ToInt32(r["oid"]);
            if (tp == "V" || tp == "U") rel[n] = oid; else if (tp == "P") pro[n] = oid; else if (tp == "FN") fn[n] = oid;
        }
        var uid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow r in users.Rows) uid[S(r["nm"])] = Convert.ToInt32(r["pid"]);
        DataTable t = Tbl("gid", "oid", "pn", "st");
        string d = Lit(db.Name);
        foreach (DataRow r in db.Query("SELECT User AS u, Host AS h, Table_name AS n, Table_priv AS p FROM mysql.tables_priv WHERE Db=" + d).Rows)
        {
            int g, o;
            if (!uid.TryGetValue(S(r["u"]) + "@" + S(r["h"]), out g)) continue;
            if (!rel.TryGetValue(S(r["n"]), out o)) continue;
            string pl = "," + S(r["p"]).ToLower() + ",";
            foreach (string pn in new[] { "select", "insert", "update", "delete", "references" }) if (pl.Contains("," + pn + ",")) t.Rows.Add(g, o, pn.ToUpper(), "G");
        }
        foreach (DataRow r in db.Query("SELECT User AS u, Host AS h, Routine_name AS n, Routine_type AS rt, Proc_priv AS p FROM mysql.procs_priv WHERE Db=" + d).Rows)
        {
            int g, o;
            if (!uid.TryGetValue(S(r["u"]) + "@" + S(r["h"]), out g)) continue;
            if (!("," + S(r["p"]).ToLower() + ",").Contains(",execute,")) continue;
            var map = S(r["rt"]).ToUpper() == "PROCEDURE" ? pro : fn;
            if (map.TryGetValue(S(r["n"]), out o)) t.Rows.Add(g, o, "EXECUTE", "G");
        }
        return t;
    }
    string Target(string nm, string tp)
    {
        string o = Q(db.Name) + "." + Q(nm);
        if (tp == "P") return "PROCEDURE " + o;
        if (tp == "FN") return "FUNCTION " + o;
        return o;
    }
    public override string Grant(string sch, string nm, string tp, string perm, string user) { return "GRANT " + perm + " ON " + Target(nm, tp) + " TO " + UserRef(user); }
    public override string Revoke(string sch, string nm, string tp, string perm, string user) { return "REVOKE " + perm + " ON " + Target(nm, tp) + " FROM " + UserRef(user); }
    public override bool UserExists(string name)
    {
        string u, h; Split(name, out u, out h);
        return db.Query("SELECT 1 FROM mysql.user WHERE User=" + Lit(u) + " AND Host=" + Lit(h)).Rows.Count > 0;
    }
    public override bool IsProtected(string name)
    {
        string u, h; Split(name, out u, out h);
        if (Array.IndexOf(SysUsers, u.ToLower()) >= 0) return true;
        DataTable t = db.Query("SELECT Super_priv AS sp, Grant_priv AS gp FROM mysql.user WHERE User=" + Lit(u) + " AND Host=" + Lit(h));
        return t.Rows.Count > 0 && (B(t.Rows[0]["sp"]) || B(t.Rows[0]["gp"]));
    }
    public override void CreateUser(string name, string pwd) { db.Exec("CREATE USER " + UserRef(name) + " IDENTIFIED BY " + Lit(pwd), "CREATE USER " + UserRef(name) + " IDENTIFIED BY '***'"); }
    public override void DropUser(string name) { db.Exec("DROP USER " + UserRef(name), null); }
    public override void ChangePassword(string name, string pwd) { db.Exec("ALTER USER " + UserRef(name) + " IDENTIFIED BY " + Lit(pwd), "ALTER USER " + UserRef(name) + " IDENTIFIED BY '***'"); }
}

// ===== настройки (хранятся в loginchanger.ini рядом с программой, ключи s_*; пароли не сохраняются) =====
static class Cfg
{
    public static string Theme = "light";                 // light | dark | auto (как в системе)
    public static bool ConfirmApply = true, ConfirmUndo = true;
    public static int PwdLen = 8;
    public static bool PwdSymbol = false, PolicyOff = false;
    public static int ConnTimeout = 10, CmdTimeout = 60;
    public static bool TrustCert = true, Encrypt = false, RememberServers = true;
    public static bool ShowDescr = true;
    public static string CsvDelim = ";", CsvEnc = "utf8bom", CsvMark = "+";
    public static bool LogToFile = true; public static int LogDays = 30;
    public static string TplDir = "";
    public static string AiUrl = "", AiKey = "", AiModel = "", RulesFile = "";
    public static int AiBatch = 15, AiMaxLen = 200, AiMaxDef = 2500, AiTimeout = 120;
    public static bool AiSendDef = true;
    public const string DefaultStyle = "Одно предложение на русском (до {max} символов): что показывает объект, ключевая сущность и период, если понятно. Без слов «таблица», «представление», «view». Для архивных (_OLD) и временных (_TEMP) копий добавляй пометку в скобках. Не повторяй имя объекта.";
    public static string AiStyle = DefaultStyle;
    public static List<string> ViewPerms = new List<string>(new[] { "SELECT" });
    public static List<string> TablePerms = new List<string>(new[] { "SELECT" });

    public static readonly string[] ViewAll = { "SELECT", "INSERT", "UPDATE", "DELETE" };
    public static readonly string[] TableAll = { "SELECT", "INSERT", "UPDATE", "DELETE", "REFERENCES" };

    static bool B(Dictionary<string, string> d, string k, bool def) { string v; return d.TryGetValue(k, out v) ? v == "1" : def; }
    static int I(Dictionary<string, string> d, string k, int def, int lo, int hi)
    {
        string v; int n;
        if (d.TryGetValue(k, out v) && int.TryParse(v, out n)) return Math.Max(lo, Math.Min(hi, n));
        return def;
    }
    static string S(Dictionary<string, string> d, string k, string def) { string v; return d.TryGetValue(k, out v) ? v : def; }
    static List<string> Perms(Dictionary<string, string> d, string k, string[] all)
    {
        var l = new List<string> { "SELECT" };
        string v; if (!d.TryGetValue(k, out v)) return l;
        foreach (string p in v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            if (Array.IndexOf(all, p) >= 0 && !l.Contains(p)) l.Add(p);
        return l;
    }

    public static void Load()
    {
        var d = App.LoadIni();
        Theme = S(d, "theme", "light"); if (Theme != "dark" && Theme != "auto") Theme = "light";
        ConfirmApply = B(d, "s_confirm_apply", true); ConfirmUndo = B(d, "s_confirm_undo", true);
        PwdLen = I(d, "s_pwd_len", 8, 6, 32); PwdSymbol = B(d, "s_pwd_symbol", false); PolicyOff = B(d, "s_policy_off", false);
        ConnTimeout = I(d, "s_conn_timeout", 10, 3, 120); CmdTimeout = I(d, "s_cmd_timeout", 60, 10, 600);
        TrustCert = B(d, "s_trust_cert", true); Encrypt = B(d, "s_encrypt", false); RememberServers = B(d, "s_remember_servers", true);
        ShowDescr = B(d, "s_show_descr", true);
        CsvDelim = S(d, "s_csv_delim", ";"); if (CsvDelim != "," && CsvDelim != "\t") CsvDelim = ";";
        CsvEnc = S(d, "s_csv_enc", "utf8bom"); if (CsvEnc != "utf8" && CsvEnc != "cp1251") CsvEnc = "utf8bom";
        CsvMark = S(d, "s_csv_mark", "+"); if (CsvMark.Length == 0 || CsvMark.Length > 3) CsvMark = "+";
        LogToFile = B(d, "s_log_file", true); LogDays = I(d, "s_log_days", 30, 1, 365);
        TplDir = S(d, "s_tpl_dir", "");
        AiUrl = S(d, "s_ai_url", ""); AiKey = Secret.Unprotect(S(d, "s_ai_key", "")); AiModel = S(d, "s_ai_model", "");
        AiBatch = I(d, "s_ai_batch", 15, 1, 50); AiSendDef = B(d, "s_ai_def", true); AiMaxLen = I(d, "s_ai_maxlen", 200, 60, 500);
        AiMaxDef = I(d, "s_ai_maxdef", 2500, 200, 8000); AiTimeout = I(d, "s_ai_timeout", 120, 10, 600);
        AiStyle = S(d, "s_ai_style", DefaultStyle).Replace("\\n", "\n"); if (AiStyle.Trim().Length == 0) AiStyle = DefaultStyle;
        RulesFile = S(d, "s_rules_file", "");
        ViewPerms = Perms(d, "s_view_perms", ViewAll); TablePerms = Perms(d, "s_table_perms", TableAll);
    }

    public static void Defaults()
    {
        Theme = "light"; ConfirmApply = true; ConfirmUndo = true; PwdLen = 8; PwdSymbol = false; PolicyOff = false;
        ConnTimeout = 10; CmdTimeout = 60; TrustCert = true; Encrypt = false; RememberServers = true; ShowDescr = true;
        CsvDelim = ";"; CsvEnc = "utf8bom"; CsvMark = "+"; LogToFile = true; LogDays = 30; TplDir = "";
        AiUrl = ""; AiKey = ""; AiModel = ""; AiBatch = 15; AiSendDef = true; AiMaxLen = 200; AiMaxDef = 2500; AiTimeout = 120; AiStyle = DefaultStyle; RulesFile = "";
        ViewPerms = new List<string>(new[] { "SELECT" }); TablePerms = new List<string>(new[] { "SELECT" });
    }

    static string F(bool b) { return b ? "1" : "0"; }
    public static void Save()
    {
        var d = App.LoadIni();
        d["theme"] = Theme;
        d["s_confirm_apply"] = F(ConfirmApply); d["s_confirm_undo"] = F(ConfirmUndo);
        d["s_pwd_len"] = PwdLen.ToString(); d["s_pwd_symbol"] = F(PwdSymbol); d["s_policy_off"] = F(PolicyOff);
        d["s_conn_timeout"] = ConnTimeout.ToString(); d["s_cmd_timeout"] = CmdTimeout.ToString();
        d["s_trust_cert"] = F(TrustCert); d["s_encrypt"] = F(Encrypt); d["s_remember_servers"] = F(RememberServers);
        d["s_show_descr"] = F(ShowDescr);
        d["s_csv_delim"] = CsvDelim; d["s_csv_enc"] = CsvEnc; d["s_csv_mark"] = CsvMark;
        d["s_log_file"] = F(LogToFile); d["s_log_days"] = LogDays.ToString();
        d["s_tpl_dir"] = TplDir;
        d["s_ai_url"] = AiUrl; d["s_ai_key"] = Secret.Protect(AiKey); d["s_ai_model"] = AiModel; d["s_ai_batch"] = AiBatch.ToString();
        d["s_ai_def"] = F(AiSendDef); d["s_ai_maxlen"] = AiMaxLen.ToString(); d["s_ai_maxdef"] = AiMaxDef.ToString(); d["s_ai_timeout"] = AiTimeout.ToString();
        d["s_ai_style"] = AiStyle.Replace("\r", "").Replace("\n", "\\n"); d["s_rules_file"] = RulesFile;
        d["s_view_perms"] = string.Join(",", ViewPerms.ToArray()); d["s_table_perms"] = string.Join(",", TablePerms.ToArray());
        if (!RememberServers) d.Remove("servers");
        App.SaveIni(d);
    }

    // тёмная ли тема сейчас (для режима «как в системе» читаем настройку Windows)
    public static bool IsDark()
    {
        if (Theme == "dark") return true;
        if (Theme == "light") return false;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                return v != null && Convert.ToInt32(v) == 0;
            }
        }
        catch { return false; }
    }
}

// ===== окно настроек =====
class SettingsForm : Form
{
    CheckBox[] vp, tp;
    NumericUpDown nPwd = new NumericUpDown(), nConn = new NumericUpDown(), nCmd = new NumericUpDown(), nLog = new NumericUpDown();
    CheckBox cSym = new CheckBox(), cPol = new CheckBox(), cCA = new CheckBox(), cCU = new CheckBox(), cTrust = new CheckBox(), cEnc = new CheckBox(),
             cRem = new CheckBox(), cDescr = new CheckBox(), cLog = new CheckBox();
    FlatCombo cDelim = new FlatCombo(), cCsvEnc = new FlatCombo(), cTheme = new FlatCombo();
    TextBox tMark = new TextBox(), tTpl = new TextBox();
    TextBox tAiUrl = new TextBox(), tAiKey = new TextBox(), tAiStyle = new TextBox(), tRules = new TextBox();
    ComboBox tAiModel = new ComboBox(); FlatCombo cPreset = new FlatCombo(); Label lPre; LinkLabel lKey; bool presetBusy;
    NumericUpDown nAiBatch = new NumericUpDown(), nAiMax = new NumericUpDown(), nAiTimeout = new NumericUpDown();
    CheckBox cAiDef = new CheckBox();
    TableLayoutPanel tl;

    public SettingsForm()
    {
        Text = "Настройки"; AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false;
        MinimumSize = new Size(620, 460); Size = new Size(760, Math.Min(780, Screen.PrimaryScreen.WorkingArea.Height - 40));

        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0) };
        tl = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(24, 12, 24, 12) };
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.Controls.Add(tl);

        vp = PermBoxes(Cfg.ViewAll); tp = PermBoxes(Cfg.TableAll);
        Section("Права в матрицах");
        Row("Представления:", PermRow(vp));
        Row("Таблицы:", PermRow(tp));
        Note("SELECT включён всегда. Если отмечено больше одного права, над матрицей появляется выбор «Право». Процедуры и функции — всегда EXECUTE (для табличных функций SQL Server сам использует SELECT).");

        Section("Пароли");
        nPwd.Minimum = 6; nPwd.Maximum = 32; nPwd.Width = 80; Row("Длина сгенерированного пароля:", nPwd);
        cSym.Text = "Добавлять спецсимвол (для строгих политик паролей)"; CheckRow(cSym);
        cPol.Text = "По умолчанию не проверять сложность пароля сервером (SQL Server)"; CheckRow(cPol);

        Section("Подтверждения");
        cCA.Text = "Спрашивать подтверждение перед «Применить»"; CheckRow(cCA);
        cCU.Text = "Спрашивать подтверждение перед «Откатить последнее»"; CheckRow(cCU);
        Note("Защита администраторов (sa, dbo, sysadmin, db_owner и т. п.) включена всегда и отключению не подлежит.");

        Section("Подключение");
        nConn.Minimum = 3; nConn.Maximum = 120; nConn.Width = 80; Row("Таймаут подключения, с:", nConn);
        nCmd.Minimum = 10; nCmd.Maximum = 600; nCmd.Width = 80; Row("Таймаут команд, с:", nCmd);
        cTrust.Text = "Доверять сертификату сервера (SQL Server)"; CheckRow(cTrust);
        cEnc.Text = "Шифровать соединение (SQL Server)"; CheckRow(cEnc);
        cRem.Text = "Запоминать список серверов"; CheckRow(cRem);

        Section("Матрица");
        cDescr.Text = "Показывать колонку «Описание» (MS_Description и комментарии)"; CheckRow(cDescr);

        Section("Экспорт CSV");
        cDelim.Items.AddRange(new object[] { "; точка с запятой (Excel, русская локаль)", ", запятая", "Табуляция" }); cDelim.Width = 340; Row("Разделитель:", cDelim);
        cCsvEnc.Items.AddRange(new object[] { "UTF-8 с BOM (Excel откроет без проблем)", "UTF-8", "Windows-1251" }); cCsvEnc.Width = 340; Row("Кодировка:", cCsvEnc);
        tMark.Width = 60; tMark.MaxLength = 3; Row("Знак «доступ есть»:", tMark);

        Section("Файлы и журнал");
        cLog.Text = "Вести журнал в файл (папка logs)"; CheckRow(cLog);
        nLog.Minimum = 1; nLog.Maximum = 365; nLog.Width = 80; Row("Хранить журнал, дней:", nLog);
        var bLogs = new Button { Text = "Открыть папку журнала", AutoSize = true };
        bLogs.Click += delegate
        {
            try { string d = Path.Combine(App.Dir, "logs"); Directory.CreateDirectory(d); System.Diagnostics.Process.Start("explorer.exe", d); } catch (Exception ex) { UI.Warn(ex.Message); }
        };
        Row("", bLogs);
        var pt = new Panel { Height = 32, MinimumSize = new Size(0, 32) };
        var bBr = new Button { Text = "Обзор…", Dock = DockStyle.Right, Width = 90 }; tTpl.Dock = DockStyle.Fill;
        pt.Controls.Add(tTpl); pt.Controls.Add(bBr);
        bBr.Click += delegate
        {
            using (var fb = new FolderBrowserDialog()) { fb.Description = "Папка шаблонов прав (.tpl)"; if (fb.ShowDialog(this) == DialogResult.OK) tTpl.Text = fb.SelectedPath; }
        };
        Row("Папка шаблонов:", pt);
        Note("Пусто — папка templates рядом с программой. Файл настроек: " + Path.Combine(App.Dir, "loginchanger.ini"));

        Section("Описания объектов и ИИ");
        cPreset.Width = 420; cPreset.Items.Add("— выберите сервис, чтобы подставить настройки —");
        foreach (var p in AiPresets.All) cPreset.Items.Add(p.Name);
        cPreset.SelectedIndex = 0; Row("Готовые настройки:", cPreset);
        lPre = Note("");
        lKey = new LinkLabel { Text = "Где взять ключ API (откроется страница сервиса)", AutoSize = true, Visible = false };
        lKey.LinkClicked += delegate { try { string k = lKey.Tag as string; if (k != null) System.Diagnostics.Process.Start(k); } catch { } };
        Row("", lKey);
        cPreset.SelectedIndexChanged += delegate { if (!presetBusy) ApplyPreset(cPreset.SelectedIndex - 1, true); };
        tAiUrl.Width = 420; Row("Адрес сервиса ИИ:", tAiUrl);
        tAiUrl.TextChanged += delegate { if (presetBusy) return; int i = AiPresets.IndexOfUrl(tAiUrl.Text); presetBusy = true; cPreset.SelectedIndex = i + 1; presetBusy = false; ApplyPreset(i, false); };
        Note("Любой OpenAI-совместимый: https://api.openai.com/v1 · http://localhost:11434/v1 (Ollama) · http://localhost:1234/v1 (LM Studio). Для локальных адресов подтверждение отправки не спрашивается.");
        tAiKey.Width = 420; tAiKey.UseSystemPasswordChar = true; Row("Ключ API:", tAiKey);
        tAiModel.DropDownStyle = ComboBoxStyle.DropDown; tAiModel.Dock = DockStyle.Fill;
        var pm = new Panel { Height = 32, MinimumSize = new Size(0, 32), Width = 420 };
        var bModels = new Button { Text = "Список моделей", Dock = DockStyle.Right, Width = 140 }; pm.Controls.Add(tAiModel); pm.Controls.Add(bModels);
        bModels.Click += delegate
        {
            ApplyToCfg(); Cursor = Cursors.WaitCursor; bModels.Enabled = false;
            try
            {
                var l = Ai.ListModels(); string cur = tAiModel.Text; tAiModel.Items.Clear(); foreach (string m in l) tAiModel.Items.Add(m); tAiModel.Text = cur;
                UI.Info("Моделей у сервиса: " + l.Count + ". Выберите нужную в выпадающем списке.");
            }
            catch (Exception ex) { UI.Warn(ex.Message); }
            finally { bModels.Enabled = true; Cursor = Cursors.Default; }
        };
        Row("Модель:", pm);
        nAiBatch.Minimum = 1; nAiBatch.Maximum = 50; nAiBatch.Width = 80; Row("Объектов в одном запросе:", nAiBatch);
        cAiDef.Text = "Отправлять тексты определений (SQL) — точнее описание, но виден код отчётов"; CheckRow(cAiDef);
        nAiMax.Minimum = 60; nAiMax.Maximum = 500; nAiMax.Width = 80; Row("Максимальная длина описания:", nAiMax);
        nAiTimeout.Minimum = 10; nAiTimeout.Maximum = 600; nAiTimeout.Width = 80; Row("Таймаут запроса, с:", nAiTimeout);
        tAiStyle.Multiline = true; tAiStyle.Width = 420; tAiStyle.Height = 78; tAiStyle.ScrollBars = ScrollBars.Vertical; Row("Требования к тексту:", tAiStyle);
        var pr = new Panel { Height = 32, MinimumSize = new Size(0, 32), Width = 420 };
        var bRb = new Button { Text = "Обзор…", Dock = DockStyle.Right, Width = 90 }; tRules.Dock = DockStyle.Fill; pr.Controls.Add(tRules); pr.Controls.Add(bRb);
        bRb.Click += delegate
        {
            using (var fd = new OpenFileDialog()) { fd.Filter = "Файлы правил (*.rules;*.txt)|*.rules;*.txt|Все файлы|*.*"; if (fd.ShowDialog(this) == DialogResult.OK) tRules.Text = fd.FileName; }
        };
        Row("Файл правил описаний:", pr);
        var bAiTest = new Button { Text = "Проверить соединение с ИИ", AutoSize = true };
        bAiTest.Click += delegate
        {
            ApplyToCfg(); bAiTest.Enabled = false; Cursor = Cursors.WaitCursor;
            try { string a = Ai.Chat("Отвечай одним словом.", "Ответь словом: ок", CancellationToken.None); UI.Info("Соединение работает. Ответ модели: " + a.Trim()); }
            catch (Exception ex) { UI.Warn(ex.Message); }
            finally { bAiTest.Enabled = true; Cursor = Cursors.Default; }
        };
        Row("", bAiTest);
        Note("Ключ хранится в зашифрованном виде (Windows DPAPI): расшифровать его может только ваша учётная запись Windows на этом компьютере. Данные таблиц на сервер ИИ не отправляются.");

        Section("Внешний вид");
        cTheme.Items.AddRange(new object[] { "Светлая", "Тёмная", "Как в системе" }); cTheme.Width = 220; Row("Тема:", cTheme);

        var foot = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 12) };
        var ok = new Button { Text = "Сохранить", Tag = "primary", AutoSize = true, DialogResult = DialogResult.OK };
        var cn = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var rs = new Button { Text = "По умолчанию", AutoSize = true, Tag = "ghost" };
        foot.Controls.Add(ok); foot.Controls.Add(cn); foot.Controls.Add(rs);
        Controls.Add(body); Controls.Add(foot);
        AcceptButton = ok; CancelButton = cn;
        ok.Click += delegate { Collect(); };
        rs.Click += delegate { Cfg.Defaults(); LoadValues(); };
        FormClosed += delegate { if (DialogResult != DialogResult.OK) Cfg.Load(); };   // отмена — возвращаем сохранённые значения
        LoadValues();
        Theme.Apply(this);
        lKey.LinkColor = Theme.Accent; lKey.ActiveLinkColor = Theme.Accent; lKey.VisitedLinkColor = Theme.Accent;
    }

    // подставляет адрес и список моделей выбранного сервиса; fill=false — только показать пояснение к уже введённому адресу
    void ApplyPreset(int i, bool fill)
    {
        if (i < 0) { lPre.Text = ""; lKey.Visible = false; return; }
        var p = AiPresets.All[i]; presetBusy = true;
        if (fill)
        {
            tAiUrl.Text = p.Url; string cur = tAiModel.Text.Trim();
            tAiModel.Items.Clear(); foreach (string m in p.Models) tAiModel.Items.Add(m);
            if (cur.Length == 0 || Array.IndexOf(p.Models, cur) < 0) tAiModel.Text = p.Models.Length > 0 ? p.Models[0] : "";
        }
        else if (tAiModel.Items.Count == 0) foreach (string m in p.Models) tAiModel.Items.Add(m);
        string host = p.Url; try { host = new Uri(p.Url).Host; } catch { }
        lPre.Text = p.Note + (p.Local ? "" : "\nДанные (имена объектов, столбцы и при включённой настройке определения) будут отправляться на серверы «" + host + "».");
        lKey.Tag = p.KeyUrl; lKey.Visible = p.KeyUrl != null; presetBusy = false;
    }

    static CheckBox[] PermBoxes(string[] all)
    {
        var l = new CheckBox[all.Length];
        for (int i = 0; i < all.Length; i++) l[i] = new CheckBox { Text = all[i], AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        l[0].Enabled = false;   // SELECT всегда
        return l;
    }
    static Control PermRow(CheckBox[] boxes)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        foreach (var b in boxes) f.Controls.Add(b);
        return f;
    }

    void Section(string title)
    {
        int r = tl.RowCount++;
        var p = new Panel { Height = 34, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 4) };
        var lbl = new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI Semibold", 11.5f), Location = new Point(0, 4) };
        var line = new Panel { Height = 1, Dock = DockStyle.Bottom, Tag = "line" };
        p.Controls.Add(lbl); p.Controls.Add(line);
        tl.Controls.Add(p, 0, r); tl.SetColumnSpan(p, 2);
    }
    void Row(string label, Control c)
    {
        int r = tl.RowCount++;
        tl.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 7) }, 0, r);
        c.Margin = new Padding(3, 4, 3, 4); c.Anchor = AnchorStyles.Left; tl.Controls.Add(c, 1, r);
    }
    void CheckRow(CheckBox c)
    {
        int r = tl.RowCount++;
        c.AutoSize = true; c.Margin = new Padding(3, 6, 3, 6);
        tl.Controls.Add(c, 0, r); tl.SetColumnSpan(c, 2);
    }
    Label Note(string text)
    {
        int r = tl.RowCount++;
        var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(640, 0), Tag = "muted", Margin = new Padding(3, 4, 3, 6) };
        tl.Controls.Add(l, 0, r); tl.SetColumnSpan(l, 2);
        return l;
    }

    static void Mark(CheckBox[] boxes, List<string> perms) { for (int i = 0; i < boxes.Length; i++) boxes[i].Checked = i == 0 || perms.Contains(boxes[i].Text); }
    static List<string> Marked(CheckBox[] boxes) { var l = new List<string> { "SELECT" }; for (int i = 1; i < boxes.Length; i++) if (boxes[i].Checked) l.Add(boxes[i].Text); return l; }

    void LoadValues()
    {
        Mark(vp, Cfg.ViewPerms); Mark(tp, Cfg.TablePerms);
        nPwd.Value = Cfg.PwdLen; cSym.Checked = Cfg.PwdSymbol; cPol.Checked = Cfg.PolicyOff;
        cCA.Checked = Cfg.ConfirmApply; cCU.Checked = Cfg.ConfirmUndo;
        nConn.Value = Cfg.ConnTimeout; nCmd.Value = Cfg.CmdTimeout; cTrust.Checked = Cfg.TrustCert; cEnc.Checked = Cfg.Encrypt; cRem.Checked = Cfg.RememberServers;
        cDescr.Checked = Cfg.ShowDescr;
        cDelim.SelectedIndex = Cfg.CsvDelim == "," ? 1 : Cfg.CsvDelim == "\t" ? 2 : 0;
        cCsvEnc.SelectedIndex = Cfg.CsvEnc == "utf8" ? 1 : Cfg.CsvEnc == "cp1251" ? 2 : 0;
        tMark.Text = Cfg.CsvMark;
        cLog.Checked = Cfg.LogToFile; nLog.Value = Cfg.LogDays; tTpl.Text = Cfg.TplDir;
        cTheme.SelectedIndex = Cfg.Theme == "dark" ? 1 : Cfg.Theme == "auto" ? 2 : 0;
        tAiUrl.Text = Cfg.AiUrl; tAiKey.Text = Cfg.AiKey; tAiModel.Text = Cfg.AiModel; nAiBatch.Value = Cfg.AiBatch; cAiDef.Checked = Cfg.AiSendDef;
        nAiMax.Value = Cfg.AiMaxLen; nAiTimeout.Value = Cfg.AiTimeout; tAiStyle.Text = Cfg.AiStyle.Replace("\n", "\r\n"); tRules.Text = Cfg.RulesFile;
    }

    void ApplyToCfg()
    {
        Cfg.ViewPerms = Marked(vp); Cfg.TablePerms = Marked(tp);
        Cfg.PwdLen = (int)nPwd.Value; Cfg.PwdSymbol = cSym.Checked; Cfg.PolicyOff = cPol.Checked;
        Cfg.ConfirmApply = cCA.Checked; Cfg.ConfirmUndo = cCU.Checked;
        Cfg.ConnTimeout = (int)nConn.Value; Cfg.CmdTimeout = (int)nCmd.Value; Cfg.TrustCert = cTrust.Checked; Cfg.Encrypt = cEnc.Checked; Cfg.RememberServers = cRem.Checked;
        Cfg.ShowDescr = cDescr.Checked;
        Cfg.CsvDelim = cDelim.SelectedIndex == 1 ? "," : cDelim.SelectedIndex == 2 ? "\t" : ";";
        Cfg.CsvEnc = cCsvEnc.SelectedIndex == 1 ? "utf8" : cCsvEnc.SelectedIndex == 2 ? "cp1251" : "utf8bom";
        Cfg.CsvMark = tMark.Text.Trim().Length == 0 ? "+" : tMark.Text.Trim();
        Cfg.LogToFile = cLog.Checked; Cfg.LogDays = (int)nLog.Value; Cfg.TplDir = tTpl.Text.Trim();
        Cfg.Theme = cTheme.SelectedIndex == 1 ? "dark" : cTheme.SelectedIndex == 2 ? "auto" : "light";
        Cfg.AiUrl = tAiUrl.Text.Trim(); Cfg.AiKey = tAiKey.Text.Trim(); Cfg.AiModel = tAiModel.Text.Trim(); Cfg.AiBatch = (int)nAiBatch.Value; Cfg.AiSendDef = cAiDef.Checked;
        Cfg.AiMaxLen = (int)nAiMax.Value; Cfg.AiTimeout = (int)nAiTimeout.Value; Cfg.AiStyle = tAiStyle.Text.Trim().Length == 0 ? Cfg.DefaultStyle : tAiStyle.Text.Replace("\r\n", "\n").Trim();
        Cfg.RulesFile = tRules.Text.Trim();
    }

    void Collect() { ApplyToCfg(); Cfg.Save(); }
}

// ===== описания объектов: модель, правила, файлы, ИИ =====
class DescItem
{
    public int Oid; public string Sch = "", Nm = "", Tp = "";
    public string Cur = "", CurSrc = "";       // что записано в базе сейчас (и откуда оно)
    public string New, NewSrc = "", NewDetail = "";   // черновик (New == null — без изменений), источник и подробность (файл, модель)
    public string CurStored = "";              // «код|подробность|дата» из базы
    public bool Local;                         // описание хранится не в БД, а в файле рядом с программой
    public string Full { get { return Sch.Length > 0 ? Sch + "." + Nm : Nm; } }
    public bool Dirty { get { return New != null && (New != Cur || (NewSrc.Length > 0 && NewSrc != CurSrc)); } }
    public string Shown { get { return New != null ? New : Cur; } }
    public string ShownSrc { get { return New != null && NewSrc.Length > 0 ? NewSrc : CurSrc; } }
    public static string Kind(string tp)
    {
        switch (tp)
        {
            case "V": return "представление";
            case "U": return "таблица";
            case "P": case "PC": return "процедура";
            case "FN": case "FS": return "функция";
            case "IF": case "TF": case "FT": return "табличная функция";
            case "AF": return "агрегатная функция";
        }
        return tp;
    }
    public static string SrcName(string src)
    {
        switch (src)
        {
            case "manual": return "вручную";
            case "file": return "файл";
            case "rules": return "правила";
            case "ai?": return "ИИ (не проверено)";
            case "ai": return "ИИ (проверено)";
        }
        return "";
    }
    // LC_Source хранится как «код|подробность|дата»
    public static string Detail(string stored) { if (stored == null) return ""; string[] p = stored.Split('|'); return p.Length > 1 ? p[1].Trim() : ""; }
    public static string SrcCode(string stored) { if (stored == null) return ""; int i = stored.IndexOf('|'); return i < 0 ? stored.Trim() : stored.Substring(0, i).Trim(); }
}

// ----- правила: «регулярка → шаблон» + глоссарий (формат повторяет логику gen.py) -----
// [strip]     префиксы, которые отбрасываются (AN_)
// [entities]  КЛЮЧ = текст: сущность в начале имени после префикса, добавляется « — текст»
// [tags]      СУФФИКС = текст: _OLD = архивная копия, добавляется « (текст)»
// [exact]     ИМЯ = текст: точное имя
// [rules]     регулярка => шаблон: {0} {1}… группы, {g1} — группа через глоссарий, {?1: текст} — если группа 1 не пуста
// [rules_other] то же, но для имён без отбрасываемого префикса (если [strip] задан)
// [extras]    регулярка => шаблон: дописывается к описанию (год, номер счёта)
// [fallback]  prefixed = … / other = … ({rest} — имя без префикса)
class RuleSet
{
    readonly List<string> strip = new List<string>();
    readonly List<KeyValuePair<string, string>> entities = new List<KeyValuePair<string, string>>();
    readonly List<KeyValuePair<string, string>> tags = new List<KeyValuePair<string, string>>();
    readonly Dictionary<string, string> exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> gloss = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    readonly List<KeyValuePair<System.Text.RegularExpressions.Regex, string>> rules = new List<KeyValuePair<System.Text.RegularExpressions.Regex, string>>();
    readonly List<KeyValuePair<System.Text.RegularExpressions.Regex, string>> extras = new List<KeyValuePair<System.Text.RegularExpressions.Regex, string>>();
    readonly List<KeyValuePair<System.Text.RegularExpressions.Regex, string>> otherRules = new List<KeyValuePair<System.Text.RegularExpressions.Regex, string>>();
    string fbPrefixed = "", fbOther = "";
    public int Count { get { return rules.Count + exact.Count + entities.Count; } }
    public Dictionary<string, string> Glossary { get { return gloss; } }

    public static RuleSet Load(string path)
    {
        var rs = new RuleSet(); string sec = "";
        foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string l = raw.Trim();
            if (l.Length == 0 || l[0] == '#') continue;
            if (l[0] == '[' && l[l.Length - 1] == ']') { sec = l.Substring(1, l.Length - 2).Trim().ToLower(); continue; }
            if (sec == "strip") { int q = l.IndexOf('='); rs.strip.Add((q >= 0 ? l.Substring(0, q) : l).Trim().ToUpperInvariant()); continue; }
            if (sec == "rules" || sec == "rules_other" || sec == "extras")
            {
                int i = l.IndexOf("=>"); if (i < 0) continue;
                var rx = new System.Text.RegularExpressions.Regex(l.Substring(0, i).Trim());
                string tpl = l.Substring(i + 2).Trim();
                if (tpl.Length >= 2 && tpl[0] == '"' && tpl[tpl.Length - 1] == '"') tpl = tpl.Substring(1, tpl.Length - 2);   // в кавычках — сохраняются пробелы по краям
                var kv = new KeyValuePair<System.Text.RegularExpressions.Regex, string>(rx, tpl);
                if (sec == "rules") rs.rules.Add(kv); else if (sec == "rules_other") rs.otherRules.Add(kv); else rs.extras.Add(kv);
                continue;
            }
            int e = l.IndexOf('='); if (e < 0) continue;
            string k = l.Substring(0, e).Trim(), v = l.Substring(e + 1).Trim();
            switch (sec)
            {
                case "entities": rs.entities.Add(new KeyValuePair<string, string>(k.ToUpperInvariant(), v)); rs.gloss[k] = v; break;
                case "tags": rs.tags.Add(new KeyValuePair<string, string>(k.ToUpperInvariant(), v)); break;
                case "exact": rs.exact[k] = v; break;
                case "glossary": rs.gloss[k] = v; break;
                case "fallback": if (k == "prefixed") rs.fbPrefixed = v; else if (k == "other") rs.fbOther = v; break;
            }
        }
        rs.entities.Sort(delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b) { return b.Key.Length - a.Key.Length; });
        return rs;
    }

    string Expand(string tpl, System.Text.RegularExpressions.Match m)
    {
        // условные вставки {?N: текст}
        tpl = System.Text.RegularExpressions.Regex.Replace(tpl, @"\{\?(\d+):((?:[^{}]|\{[^{}]*\})*)\}", delegate(System.Text.RegularExpressions.Match c)
        {
            int n = int.Parse(c.Groups[1].Value);
            return m != null && n < m.Groups.Count && m.Groups[n].Success && m.Groups[n].Value.Length > 0 ? c.Groups[2].Value : "";
        });
        tpl = System.Text.RegularExpressions.Regex.Replace(tpl, @"\{g(\d+)\}", delegate(System.Text.RegularExpressions.Match c)
        {
            int n = int.Parse(c.Groups[1].Value); string s = m != null && n < m.Groups.Count ? m.Groups[n].Value : "";
            string g; return gloss.TryGetValue(s, out g) ? g : s;
        });
        tpl = System.Text.RegularExpressions.Regex.Replace(tpl, @"\{(\d+)\}", delegate(System.Text.RegularExpressions.Match c)
        {
            int n = int.Parse(c.Groups[1].Value); return m != null && n < m.Groups.Count ? m.Groups[n].Value : "";
        });
        return tpl;
    }

    // null — правил для этого имени нет (и нет запасного варианта)
    public string Describe(string name)
    {
        string orig = name.ToUpperInvariant(), n = orig, tag = null, d;
        if (exact.TryGetValue(orig, out d)) return d;
        foreach (var t in tags) if (n.EndsWith(t.Key) && n.Length > t.Key.Length) { n = n.Substring(0, n.Length - t.Key.Length); tag = t.Value; }
        string res;
        if (exact.TryGetValue(n, out res)) return tag != null ? res + " (" + tag + ")" : res;
        string rest = n; bool pre = false;
        foreach (string p in strip) if (rest.StartsWith(p)) { rest = rest.Substring(p.Length); pre = true; break; }
        string ent = null;
        if (pre || strip.Count == 0)
            foreach (var e in entities)
                if (rest == e.Key || rest.StartsWith(e.Key + "_") || System.Text.RegularExpressions.Regex.IsMatch(rest, "^" + System.Text.RegularExpressions.Regex.Escape(e.Key) + @"\d"))
                { ent = e.Value; rest = rest.Substring(e.Key.Length).TrimStart('_'); break; }
        string topic = null;
        foreach (var r in (pre || strip.Count == 0 ? rules : otherRules))
        {
            var m = r.Key.Match(rest);
            if (m.Success) { topic = Expand(r.Value, m); break; }
        }
        if (topic == null)
        {
            string fb = pre ? fbPrefixed : fbOther;
            if (fb.Length == 0) return null;
            topic = fb.Replace("{rest}", rest);
        }
        if (pre) foreach (var x in extras) { var m = x.Key.Match(rest); if (m.Success) topic += Expand(x.Value, m); }
        res = topic + (ent != null ? " — " + ent : "");
        return tag != null ? res + " (" + tag + ")" : res;
    }
}

// ----- чтение файлов CSV / XLSX -----
static class FileImport
{
    public static List<string[]> Read(string path)
    {
        string ext = Path.GetExtension(path).ToLower();
        return ext == ".xlsx" || ext == ".xlsm" ? ReadXlsx(path) : ReadCsv(path);
    }

    static string DecodeText(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch { return Encoding.GetEncoding(1251).GetString(b); }
    }

    public static List<string[]> ReadCsv(string path)
    {
        string text = DecodeText(File.ReadAllBytes(path));
        int nl = text.IndexOf('\n'); string first = nl < 0 ? text : text.Substring(0, nl);
        char d = ';'; int best = -1;
        foreach (char c in new[] { ';', '\t', ',' }) { int k = first.Split(c).Length; if (k > best) { best = k; d = c; } }
        var rows = new List<string[]>(); var cells = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (q)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                else sb.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == d) { cells.Add(sb.ToString()); sb.Length = 0; }
            else if (ch == '\r') { }
            else if (ch == '\n') { cells.Add(sb.ToString()); sb.Length = 0; if (cells.Count > 1 || cells[0].Length > 0) rows.Add(cells.ToArray()); cells = new List<string>(); }
            else sb.Append(ch);
        }
        if (sb.Length > 0 || cells.Count > 0) { cells.Add(sb.ToString()); rows.Add(cells.ToArray()); }
        return rows;
    }

    // XLSX = zip с XML; читаем без сторонних библиотек (System.IO.Packaging входит в .NET Framework 4)
    public static List<string[]> ReadXlsx(string path)
    {
        var rows = new List<string[]>();
        // читаем копию: исходный файл может быть открыт в Excel, а Package.Open допускает только FileShare.Read/None
        string tmp = Path.Combine(Path.GetTempPath(), "lc_" + Guid.NewGuid().ToString("N") + ".xlsx");
        using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write)) src.CopyTo(dst);
        try
        {
        using (var pkg = System.IO.Packaging.Package.Open(tmp, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var shared = new List<string>(); string sheetPath = null; var sheets = new List<string>();
            foreach (var part in pkg.GetParts())
            {
                string u = part.Uri.ToString();
                if (u.Equals("/xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase))
                    using (var s = part.GetStream()) foreach (var si in XDocument.Load(s).Descendants(ns + "si")) { var sbb = new StringBuilder(); foreach (var t in si.Descendants(ns + "t")) sbb.Append(t.Value); shared.Add(sbb.ToString()); }
                else if (u.StartsWith("/xl/worksheets/", StringComparison.OrdinalIgnoreCase) && u.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) sheets.Add(u);
            }
            sheets.Sort(); if (sheets.Count == 0) return rows; sheetPath = sheets[0];
            using (var s = pkg.GetPart(new Uri(sheetPath, UriKind.Relative)).GetStream())
                foreach (var row in XDocument.Load(s).Descendants(ns + "row"))
                {
                    var cells = new Dictionary<int, string>(); int max = -1;
                    foreach (var c in row.Elements(ns + "c"))
                    {
                        string r = (string)c.Attribute("r") ?? ""; int col = 0, i = 0;
                        while (i < r.Length && char.IsLetter(r[i])) { col = col * 26 + (char.ToUpper(r[i]) - 'A' + 1); i++; }
                        col--; if (col < 0) col = cells.Count;
                        string t = (string)c.Attribute("t"), val = "";
                        if (t == "s") { var v = c.Element(ns + "v"); int idx; if (v != null && int.TryParse(v.Value, out idx) && idx < shared.Count) val = shared[idx]; }
                        else if (t == "inlineStr") { var sbb = new StringBuilder(); foreach (var x in c.Descendants(ns + "t")) sbb.Append(x.Value); val = sbb.ToString(); }
                        else { var v = c.Element(ns + "v"); val = v != null ? v.Value : ""; }
                        cells[col] = val; if (col > max) max = col;
                    }
                    if (max < 0) continue;
                    var arr = new string[max + 1]; for (int k = 0; k <= max; k++) { string v; arr[k] = cells.TryGetValue(k, out v) ? v : ""; }
                    rows.Add(arr);
                }
        }
        }
        finally { try { File.Delete(tmp); } catch { } }
        return rows;
    }
}

// ----- хранение ключа API (шифрование Windows DPAPI: расшифровать может только этот пользователь Windows) -----
static class Secret
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try { return "dpapi:" + Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith("dpapi:")) return "";
        try { return Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(6)), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

// ----- клиент OpenAI-совместимого сервиса (OpenAI, Azure-совместимые шлюзы, Ollama, LM Studio, vLLM и т. п.) -----
static class Ai
{
    static string Endpoint()
    {
        string u = Cfg.AiUrl.Trim().TrimEnd('/');
        return u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? u : u + "/chat/completions";
    }

    // возвращает текст ответа модели; бросает исключение с понятным сообщением
    public static string Chat(string system, string user, CancellationToken ct) { return Chat(system, user, ct, true); }

    // adapter: часть моделей (например, новые у OpenAI) принимает только температуру по умолчанию — тогда повторяем без неё
    static string Chat(string system, string user, CancellationToken ct, bool useTemp)
    {
        if (Cfg.AiUrl.Trim().Length == 0) throw new Exception("Не задан адрес сервиса ИИ (Настройки → Описания и ИИ).");
        if (Cfg.AiModel.Trim().Length == 0) throw new Exception("Не задана модель (Настройки → Описания и ИИ).");
        try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)(3072 | 768 | 192); } catch { }
        System.Net.ServicePointManager.Expect100Continue = false;
        var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var body = new Dictionary<string, object>();
        body["model"] = Cfg.AiModel.Trim(); if (useTemp) body["temperature"] = 0.2;
        body["messages"] = new object[] {
            new Dictionary<string, object> { { "role", "system" }, { "content", system } },
            new Dictionary<string, object> { { "role", "user" }, { "content", user } } };
        byte[] data = Encoding.UTF8.GetBytes(ser.Serialize(body));
        var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(Endpoint());
        req.Method = "POST"; req.ContentType = "application/json; charset=utf-8"; req.Accept = "application/json";
        req.Timeout = Cfg.AiTimeout * 1000; req.ReadWriteTimeout = Cfg.AiTimeout * 1000; req.UserAgent = "LoginChanger";
        if (Cfg.AiKey.Length > 0) req.Headers["Authorization"] = "Bearer " + Cfg.AiKey;
        string resp;
        try
        {
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            ct.Register(delegate { try { req.Abort(); } catch { } });
            using (var r = (System.Net.HttpWebResponse)req.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) resp = sr.ReadToEnd();
        }
        catch (System.Net.WebException ex)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException();
            string detail = ex.Message;
            if (ex.Response != null)
                try
                {
                    var hr = (System.Net.HttpWebResponse)ex.Response;
                    using (var sr = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) { string t = sr.ReadToEnd(); detail = (int)hr.StatusCode + " " + hr.StatusDescription + ": " + (t.Length > 400 ? t.Substring(0, 400) : t); }
                }
                catch { }
            if (useTemp && detail.StartsWith("400") && detail.ToLower().Contains("temperature")) return Chat(system, user, ct, false);
            throw new Exception("Сервис ИИ вернул ошибку: " + detail);
        }
        var root = ser.DeserializeObject(resp) as Dictionary<string, object>;
        if (root == null || !root.ContainsKey("choices")) throw new Exception("Неожиданный ответ сервиса ИИ: " + (resp.Length > 300 ? resp.Substring(0, 300) : resp));
        var choices = root["choices"] as object[];
        if (choices == null || choices.Length == 0) throw new Exception("Сервис ИИ вернул пустой ответ.");
        var msg = ((Dictionary<string, object>)choices[0])["message"] as Dictionary<string, object>;
        return msg != null && msg.ContainsKey("content") ? Convert.ToString(msg["content"]) : "";
    }

    // список моделей у сервиса: GET {адрес}/models (стандарт OpenAI, поддерживают Ollama, LM Studio, OpenRouter, Groq и др.)
    public static List<string> ListModels()
    {
        if (Cfg.AiUrl.Trim().Length == 0) throw new Exception("Сначала укажите адрес сервиса.");
        try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)(3072 | 768 | 192); } catch { }
        string u = Cfg.AiUrl.Trim().TrimEnd('/');
        if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) u = u.Substring(0, u.Length - "/chat/completions".Length);
        var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(u + "/models");
        req.Method = "GET"; req.Accept = "application/json"; req.Timeout = Math.Min(Cfg.AiTimeout, 30) * 1000; req.UserAgent = "LoginChanger";
        if (Cfg.AiKey.Length > 0) req.Headers["Authorization"] = "Bearer " + Cfg.AiKey;
        string resp;
        try { using (var r = (System.Net.HttpWebResponse)req.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) resp = sr.ReadToEnd(); }
        catch (System.Net.WebException ex)
        {
            string detail = ex.Message;
            if (ex.Response != null) try { var hr = (System.Net.HttpWebResponse)ex.Response; detail = (int)hr.StatusCode + " " + hr.StatusDescription; } catch { }
            throw new Exception("Не удалось получить список моделей: " + detail);
        }
        var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var root = ser.DeserializeObject(resp) as Dictionary<string, object>; var res = new List<string>();
        object data; if (root == null || !root.TryGetValue("data", out data)) throw new Exception("Сервис не вернул список моделей в стандартном формате.");
        foreach (object o in (object[])data) { var d = o as Dictionary<string, object>; if (d != null && d.ContainsKey("id")) { string id = Convert.ToString(d["id"]); if (id.StartsWith("models/")) id = id.Substring(7); res.Add(id); } }
        res.Sort(StringComparer.OrdinalIgnoreCase); return res;
    }

    // из ответа модели достаём {"имя": "описание"} (допускаем обрамление ```json и пояснения вокруг)
    public static Dictionary<string, string> ParseDescriptions(string content)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int a = content.IndexOf('{'), b = content.LastIndexOf('}');
        if (a < 0 || b <= a) return res;
        var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var root = ser.DeserializeObject(content.Substring(a, b - a + 1)) as Dictionary<string, object>;
        if (root == null) return res;
        object inner;
        var map = root.TryGetValue("descriptions", out inner) ? inner as Dictionary<string, object> : root;
        if (map == null) { var arr = inner as object[]; if (arr != null) foreach (object o in arr) { var d = o as Dictionary<string, object>; if (d != null && d.ContainsKey("name")) res[Convert.ToString(d["name"])] = d.ContainsKey("description") ? Convert.ToString(d["description"]) : ""; } return res; }
        foreach (var kv in map) if (kv.Value is string) res[kv.Key] = (string)kv.Value;
        return res;
    }
}

// описания, которые нельзя хранить в самой СУБД (представления MySQL): файл рядом с программой
static class Sidecar
{
    static string FileFor(Db db)
    {
        string n = db.Kind + "_" + db.Server + "_" + db.Name; foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return Path.Combine(Path.Combine(App.Dir, "descriptions"), n + ".tsv");
    }
    public static Dictionary<string, string> Load(Db db)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try { string f = FileFor(db); if (File.Exists(f)) foreach (string l in File.ReadAllLines(f, Encoding.UTF8)) { int i = l.IndexOf('\t'); if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1); } } catch { }
        return d;
    }
    public static void Set(Db db, string nm, string text)
    {
        var d = Load(db);
        if (text.Length == 0) d.Remove(nm); else d[nm] = text.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        string f = FileFor(db); Directory.CreateDirectory(Path.GetDirectoryName(f));
        var sb = new StringBuilder(); foreach (var kv in d) sb.AppendLine(kv.Key + "\t" + kv.Value);
        File.WriteAllText(f, sb.ToString(), new UTF8Encoding(false));
    }
}
// ===== вкладка «Описания» =====
class DescriptionsTab : Panel
{
    Db db; DataGridView g = new DataGridView();
    List<DescItem> items = new List<DescItem>(); DataTable objTable;
    HintBox tf = new HintBox(); FlatCombo cType = new FlatCombo(), cStat = new FlatCombo(), cMode = new FlatCombo();
    Label lInfo = new Label();
    Button bWrite, bAi, bCancel, bUndo;
    public bool Stale = true; public event Action Changed;
    bool loading; CancellationTokenSource aiCts; bool aiBusy; static bool aiConsent;
    string aiMessage = "";
    class Hist { public string Title; public List<DescItem> Items = new List<DescItem>(); public List<string> OldText = new List<string>(), OldStored = new List<string>(); }
    List<Hist> hist = new List<Hist>();
    static readonly string[] AllTypes = { "V", "U", "P", "PC", "FN", "IF", "TF", "FS", "FT", "AF" };
    public List<DescItem> Items { get { return items; } }
    public bool AiBusy { get { return aiBusy; } }
    public string AiMessage { get { return aiMessage; } }

    public DescriptionsTab(Db db)
    {
        this.db = db;
        g.Dock = DockStyle.Fill; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.AllowUserToResizeRows = false;
        g.BackgroundColor = SystemColors.Window; g.RowHeadersVisible = false; g.MultiSelect = true; g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2; g.ColumnHeadersHeight = 34;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Объект", Width = 260, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Тип", Width = 130, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Сейчас в базе", Width = 330, ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45, MinimumWidth = 160 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Новое описание (двойной клик или F2)", Width = 430, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 55, MinimumWidth = 220 });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Источник", Width = 190, ReadOnly = true });
        foreach (DataGridViewColumn c in g.Columns) c.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        g.CellEndEdit += CellEdited;
        g.DataError += delegate { };

        var filters = FlowRow(); filters.Dock = DockStyle.Top; filters.Padding = new Padding(0, 4, 0, 2);
        tf.Width = 280; tf.Margin = new Padding(0, 4, 8, 3); tf.Hint = "Поиск по объектам и описаниям…"; tf.TextChanged += delegate { ApplyFilter(); };
        cType.Items.AddRange(new object[] { "Все типы", "Представления", "Таблицы", "Процедуры", "Функции" }); cType.SelectedIndex = 0; cType.Width = 150; cType.Margin = new Padding(0, 3, 8, 3);
        cStat.Items.AddRange(new object[] { "Все", "Без описания", "С описанием", "Изменённые", "ИИ: не проверено", "Хранятся локально" }); cStat.SelectedIndex = 0; cStat.Width = 180; cStat.Margin = new Padding(0, 3, 24, 3);
        cType.SelectedIndexChanged += delegate { ApplyFilter(); }; cStat.SelectedIndexChanged += delegate { ApplyFilter(); };
        var lm = new Label { Text = "Заполнять:", AutoSize = true, Margin = new Padding(0, 9, 6, 3) };
        cMode.Items.AddRange(new object[] { "только пустые", "перезаписывать" }); cMode.SelectedIndex = 0; cMode.Width = 160; cMode.Margin = new Padding(0, 3, 8, 3);
        filters.Controls.Add(tf); filters.Controls.Add(cType); filters.Controls.Add(cStat); filters.Controls.Add(lm); filters.Controls.Add(cMode);

        var actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 2, 0, 6) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = FlowRow(); left.Dock = DockStyle.Fill; var right = FlowRow(); right.WrapContents = false; right.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        bWrite = AddBtn(left, "Записать в базу", delegate { Write(); }, "primary");
        AddBtn(left, "Сбросить", delegate { DropDrafts(); }, "ghost");
        bUndo = AddBtn(left, "Откатить последнее", delegate { Undo(); }, "ghost");
        AddBtn(left, "Из файла…", delegate { ImportDialog(); }, "ghost");
        AddBtn(left, "По правилам…", delegate { RulesDialog(); }, "ghost");
        bAi = AddBtn(left, "Предложить ИИ…", delegate { StartAi(Targets()); }, "ghost");
        bCancel = AddBtn(left, "Остановить ИИ", delegate { if (aiCts != null) aiCts.Cancel(); }, "ghost"); bCancel.Visible = false;
        AddBtn(left, "Отметить проверенными", delegate { MarkReviewed(Targets()); }, "ghost");
        AddBtn(right, "Экспорт CSV", delegate { ExportCsvDialog(); }, "ghost");
        AddBtn(right, "SQL-скрипт…", delegate { ExportSqlDialog(); }, "ghost");
        AddBtn(right, "Обновить", delegate { Reload(true); }, "ghost").Margin = new Padding(0, 3, 0, 3);
        actions.Controls.Add(left, 0, 0); actions.Controls.Add(right, 1, 0);

        lInfo.Tag = "muted"; lInfo.AutoSize = true; lInfo.Dock = DockStyle.Top; lInfo.Margin = new Padding(0, 6, 3, 8); lInfo.Padding = new Padding(0, 0, 0, 8);
        var hint = new Label { Text = "Правьте описание в колонке «Новое» (или выделите строки и заполните из файла, по правилам или с помощью ИИ). Ничего не пишется в базу, пока вы не нажмёте «Записать в базу»; можно откатить.", AutoSize = true, Tag = "muted", Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 6) };
        SizeChanged += delegate { lInfo.MaximumSize = hint.MaximumSize = new Size(Math.Max(300, ClientSize.Width - 8), 0); };
        Controls.Add(g); Controls.Add(hint); Controls.Add(lInfo); Controls.Add(actions); Controls.Add(filters);
    }

    static FlowLayoutPanel FlowRow() { return new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), Padding = new Padding(0) }; }
    static Button AddBtn(Control p, string t, EventHandler h, string tag)
    {
        var b = new Button { Text = t, AutoSize = true, Tag = tag, Margin = new Padding(0, 3, 8, 3) }; b.Click += h; p.Controls.Add(b); return b;
    }

    // ---------- загрузка ----------
    public void Reload(bool ask)
    {
        if (ask && DirtyCount() > 0 && !UI.Ask("Есть неприменённые изменения (" + DirtyCount() + "). Отбросить?")) return;
        try
        {
            Cursor = Cursors.WaitCursor; loading = true;
            objTable = db.D.Objects(AllTypes);
            bool hasSrc = objTable.Columns.Contains("src"), hasDescr = objTable.Columns.Contains("descr");
            items.Clear();
            foreach (DataRow r in objTable.Rows)
            {
                var it = new DescItem { Oid = Convert.ToInt32(r["oid"]), Sch = Convert.ToString(r["sch"]), Nm = Convert.ToString(r["nm"]), Tp = Convert.ToString(r["tp"]) };
                it.Cur = hasDescr ? Convert.ToString(r["descr"]).Trim() : "";
                it.CurStored = hasSrc ? Convert.ToString(r["src"]).Trim() : "";
                it.CurSrc = DescItem.SrcCode(it.CurStored); it.Local = db.D.DescLocal(it.Tp);
                items.Add(it);
            }
            Rebuild(); Stale = false;
        }
        catch (Exception ex) { App.Log("Ошибка загрузки описаний: " + ex.Message); UI.Warn(ex.Message); }
        finally { loading = false; Cursor = Cursors.Default; }
    }

    int DirtyCount() { int n = 0; foreach (var it in items) if (it.Dirty) n++; return n; }

    void Rebuild()
    {
        g.CurrentCell = null; g.Rows.Clear();
        foreach (var it in items) { int i = g.Rows.Add(it.Full, DescItem.Kind(it.Tp) + (it.Local ? " (локально)" : ""), it.Cur, it.New ?? "", ""); g.Rows[i].Tag = it; PaintRow(g.Rows[i]); }
        g.ClearSelection(); ApplyFilter();
    }

    static Color AiColor { get { return Theme.Dark ? Color.FromArgb(70, 55, 110) : Color.FromArgb(232, 222, 255); } }

    void PaintRow(DataGridViewRow r)
    {
        var it = (DescItem)r.Tag;
        r.Cells[2].Value = it.Cur; r.Cells[3].Value = it.New ?? ""; r.Cells[4].Value = DescItem.SrcName(it.ShownSrc);
        r.Cells[0].Style.BackColor = it.Shown.Length == 0 ? Theme.NoneRow : Color.Empty;
        r.Cells[3].Style.BackColor = it.Dirty ? Theme.Pending : Color.Empty;
        r.Cells[4].Style.BackColor = it.ShownSrc == "ai?" ? AiColor : Color.Empty;
        r.Cells[2].ToolTipText = it.Cur; r.Cells[3].ToolTipText = it.New ?? "";
    }

    void CellEdited(object s, DataGridViewCellEventArgs e)
    {
        if (loading || e.RowIndex < 0 || e.ColumnIndex != 3) return;
        var r = g.Rows[e.RowIndex]; var it = (DescItem)r.Tag;
        string v = Convert.ToString(r.Cells[3].Value ?? "").Trim();
        if (v == it.Cur && v.Length > 0) { it.New = null; it.NewSrc = ""; it.NewDetail = ""; }
        else if (v.Length == 0 && it.Cur.Length == 0) { it.New = null; it.NewSrc = ""; it.NewDetail = ""; }
        else { it.New = v; it.NewSrc = "manual"; it.NewDetail = ""; }
        PaintRow(r); UpdateInfo();
    }

    // ---------- фильтры ----------
    public void ApplyFilter()
    {
        if (g.Rows.Count == 0) { UpdateInfo(); return; }
        var words = tf.Text.ToLower().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        int ty = cType.SelectedIndex, st = cStat.SelectedIndex;
        g.CurrentCell = null;
        foreach (DataGridViewRow r in g.Rows)
        {
            var it = (DescItem)r.Tag; bool ok = true;
            string hay = (it.Full + " " + it.Shown).ToLower();
            foreach (string w in words) if (hay.IndexOf(w) < 0) { ok = false; break; }
            if (ok && ty > 0) ok = ty == 1 ? it.Tp == "V" : ty == 2 ? it.Tp == "U" : ty == 3 ? (it.Tp == "P" || it.Tp == "PC") : (it.Tp != "V" && it.Tp != "U" && it.Tp != "P" && it.Tp != "PC");
            if (ok && st > 0) ok = st == 1 ? it.Shown.Length == 0 : st == 2 ? it.Shown.Length > 0 : st == 3 ? it.Dirty : st == 4 ? it.ShownSrc == "ai?" : it.Local;
            r.Visible = ok;
        }
        UpdateInfo();
    }

    void UpdateInfo()
    {
        int vis = 0, none = 0, dirty = 0, ai = 0;
        foreach (DataGridViewRow r in g.Rows) if (r.Visible) vis++;
        foreach (var it in items) { if (it.Shown.Length == 0) none++; if (it.Dirty) dirty++; if (it.ShownSrc == "ai?") ai++; }
        lInfo.Text = "Показано " + vis + " из " + items.Count + "  ·  без описания: " + none + "  ·  изменений к записи: " + dirty + (ai > 0 ? "  ·  ИИ, не проверено: " + ai : "") + (aiMessage.Length > 0 ? "  ·  " + aiMessage : "");
        if (bWrite != null) bWrite.Text = dirty > 0 ? "Записать в базу (" + dirty + ")" : "Записать в базу";
    }

    // с чем работают «из файла / по правилам / ИИ»: выделенные строки, а если ничего не выделено — все видимые
    public List<DescItem> Targets()
    {
        var l = new List<DescItem>();
        foreach (DataGridViewRow r in g.SelectedRows) if (r.Visible) l.Add((DescItem)r.Tag);
        if (l.Count >= 2) return l;
        foreach (DataGridViewRow r in g.Rows) if (r.Visible) l.Add((DescItem)r.Tag);
        return l;
    }

    bool OnlyEmpty { get { return cMode.SelectedIndex == 0; } }
    public void SetMode(bool onlyEmpty) { cMode.SelectedIndex = onlyEmpty ? 0 : 1; }
    public void SetTypeFilter(int i) { cType.SelectedIndex = i; }

    // кладёт черновик, учитывая режим «только пустые»
    bool Put(DescItem it, string text, string src, string detail)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return false;
        if (OnlyEmpty && it.Shown.Length > 0) return false;
        if (text == it.Cur && src == it.CurSrc) return false;
        if (text == it.Cur) { it.New = null; it.NewSrc = ""; it.NewDetail = ""; return false; }
        it.New = text; it.NewSrc = src; it.NewDetail = detail ?? ""; return true;
    }

    void RefreshAll() { foreach (DataGridViewRow r in g.Rows) PaintRow(r); ApplyFilter(); }

    public void DropDrafts()
    {
        foreach (var it in items) { it.New = null; it.NewSrc = ""; it.NewDetail = ""; }
        RefreshAll();
    }

    // ---------- из файла ----------
    static string Norm(string s)
    {
        s = (s ?? "").Trim().Replace("[", "").Replace("]", "").Replace("\"", "").Replace("`", "");
        return s.ToLower();
    }
    Dictionary<string, DescItem> Index(List<DescItem> src)
    {
        var d = new Dictionary<string, DescItem>();
        foreach (var it in src) { string f = Norm(it.Full), n = Norm(it.Nm); if (!d.ContainsKey(f)) d[f] = it; if (!d.ContainsKey(n)) d[n] = it; }
        return d;
    }

    void ImportDialog()
    {
        using (var d = new OpenFileDialog())
        {
            d.Filter = "Таблицы (*.xlsx;*.csv;*.txt)|*.xlsx;*.csv;*.txt|Все файлы|*.*"; d.Title = "Файл с описаниями: объект и описание";
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try { string rep; ImportFrom(d.FileName, Targets(), out rep); UI.Info(rep); }
            catch (Exception ex) { App.Log("ОШИБКА импорта: " + ex.Message); UI.Warn("Не удалось прочитать файл:\n" + ex.Message); }
        }
    }

    // два первых столбца: имя объекта и описание. Возвращает число применённых
    public int ImportFrom(string path, List<DescItem> targets, out string report)
    {
        var rows = FileImport.Read(path);
        var idx = Index(targets); var all = Index(items);
        int ok = 0, same = 0, skipped = 0, unk = 0, total = 0; var miss = new List<string>();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; if (r.Length < 2) continue;
            string name = r[0].Trim(), text = r[1].Trim(); if (name.Length == 0) continue;
            DescItem it;
            if (i == 0 && !all.ContainsKey(Norm(name))) continue;       // строка заголовка
            total++;
            if (!all.TryGetValue(Norm(name), out it)) { unk++; if (miss.Count < 8) miss.Add(name); continue; }
            if (!idx.ContainsKey(Norm(name))) { skipped++; continue; }   // объект вне выбранного набора
            if (text.Length == 0) continue;
            if (Put(it, text, "file", Path.GetFileName(path))) ok++; else if (it.Shown == text) same++; else skipped++;
        }
        RefreshAll();
        report = "Файл: " + Path.GetFileName(path) + "\nСтрок с данными: " + total + "\nПрименено к черновику: " + ok + "\nУже совпадает: " + same + "\nПропущено (есть описание в режиме «только пустые» или вне выбора): " + skipped +
                 "\nНе найдено в базе: " + unk + (miss.Count > 0 ? " (например: " + string.Join(", ", miss.ToArray()) + ")" : "") + "\n\nПроверьте колонку «Новое описание» и нажмите «Записать в базу».";
        App.Log("Импорт описаний из файла: " + Path.GetFileName(path) + ", применено " + ok);
        return ok;
    }

    // ---------- по правилам ----------
    void RulesDialog()
    {
        using (var d = new OpenFileDialog())
        {
            d.Filter = "Файлы правил (*.rules;*.txt)|*.rules;*.txt|Все файлы|*.*"; d.Title = "Файл правил описаний";
            if (Cfg.RulesFile.Length > 0 && File.Exists(Cfg.RulesFile)) { d.InitialDirectory = Path.GetDirectoryName(Cfg.RulesFile); d.FileName = Path.GetFileName(Cfg.RulesFile); }
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try { Cfg.RulesFile = d.FileName; Cfg.Save(); string rep; ApplyRules(RuleSet.Load(d.FileName), Targets(), d.FileName, out rep); UI.Info(rep); }
            catch (Exception ex) { App.Log("ОШИБКА правил: " + ex.Message); UI.Warn("Не удалось применить правила:\n" + ex.Message); }
        }
    }

    public int ApplyRules(RuleSet rs, List<DescItem> targets, string file, out string report)
    {
        int ok = 0, none = 0, skipped = 0;
        foreach (var it in targets)
        {
            string t = rs.Describe(it.Nm);
            if (t == null) { none++; continue; }
            if (Put(it, t, "rules", Path.GetFileName(file))) ok++; else skipped++;
        }
        RefreshAll();
        report = "Правила: " + Path.GetFileName(file) + "\nОбъектов: " + targets.Count + "\nОписаний предложено: " + ok + "\nПропущено (уже есть описание или совпадает): " + skipped + "\nНет подходящего правила: " + none +
                 "\n\nПроверьте колонку «Новое описание» и нажмите «Записать в базу».";
        App.Log("Описания по правилам: " + Path.GetFileName(file) + ", предложено " + ok);
        return ok;
    }

    // ---------- ИИ ----------
    static readonly string AiSystem = "Ты помогаешь администратору базы данных документировать объекты (представления, таблицы, процедуры, функции) так, чтобы их понимали сотрудники, не знающие SQL. " +
        "Отвечай только JSON без пояснений. Не выдумывай того, чего нельзя понять из названия, столбцов и определения.";

    // собирает запросы (на потоке интерфейса), отправляет в фоне, результаты складывает в черновик
    public void StartAi(List<DescItem> targets)
    {
        if (aiBusy) return;
        var list = new List<DescItem>();
        foreach (var it in targets) if (!OnlyEmpty || it.Shown.Length == 0) list.Add(it);
        if (list.Count == 0) { UI.Info("Нет объектов для ИИ: в режиме «только пустые» у выбранных уже есть описания."); return; }
        if (Cfg.AiUrl.Trim().Length == 0 || Cfg.AiModel.Trim().Length == 0)
        { UI.Warn("Сначала настройте ИИ: Настройки → «Описания и ИИ» (адрес сервиса, модель, при необходимости ключ)."); return; }
        bool local = false;
        try { var u = new Uri(Cfg.AiUrl.Trim()); local = u.IsLoopback; } catch { }
        if (!local && !aiConsent)
        {
            string host = Cfg.AiUrl; try { host = new Uri(Cfg.AiUrl.Trim()).Host; } catch { }
            if (!UI.Ask("Будут отправлены на «" + host + "» (модель " + Cfg.AiModel + "):\n  • имена и столбцы " + list.Count + " объектов" + (Cfg.AiSendDef ? "\n  • тексты определений (SQL) представлений и процедур" : "") +
                "\n  • ваш глоссарий и примеры существующих описаний\n\nДанные из таблиц не отправляются.\nПродолжить?")) return;
            aiConsent = true;
        }
        DataTable meta;
        try { meta = db.D.Meta(objTable, Cfg.AiSendDef ? Cfg.AiMaxDef : 0); }
        catch (Exception ex) { UI.Warn("Не удалось прочитать структуру объектов:\n" + ex.Message); return; }
        var cols = new Dictionary<int, string>(); var defs = new Dictionary<int, string>();
        foreach (DataRow r in meta.Rows) { int o = Convert.ToInt32(r["oid"]); cols[o] = Convert.ToString(r["cols"]); defs[o] = Convert.ToString(r["def"]); }
        var gl = new StringBuilder();
        if (Cfg.RulesFile.Length > 0 && File.Exists(Cfg.RulesFile))
            try { int n = 0; foreach (var kv in RuleSet.Load(Cfg.RulesFile).Glossary) { gl.AppendLine("- " + kv.Key + " = " + kv.Value); if (++n >= 60) break; } } catch { }
        var prompts = new List<string>(); var names = new List<List<DescItem>>();
        int batch = Math.Max(1, Cfg.AiBatch), maxLen = Cfg.AiMaxLen;
        for (int i = 0; i < list.Count; i += batch)
        {
            var part = list.GetRange(i, Math.Min(batch, list.Count - i));
            var sb = new StringBuilder();
            sb.AppendLine("Для каждого объекта базы данных (" + db.D.Title + ") напиши краткое описание на русском.");
            sb.AppendLine("Стиль: " + Cfg.AiStyle.Replace("{max}", maxLen.ToString()));
            if (gl.Length > 0) { sb.AppendLine("Глоссарий сокращений:"); sb.Append(gl.ToString()); }
            var ex = Examples(part[0], 6);
            if (ex.Count > 0) { sb.AppendLine("Примеры уже принятых описаний:"); foreach (var e in ex) sb.AppendLine("- " + e.Nm + ": " + e.Cur); }
            sb.AppendLine("Объекты:");
            foreach (var it in part)
            {
                string c, d; cols.TryGetValue(it.Oid, out c); defs.TryGetValue(it.Oid, out d);
                sb.AppendLine("### " + it.Nm + " (" + DescItem.Kind(it.Tp) + ")");
                if (!string.IsNullOrEmpty(c)) sb.AppendLine("Столбцы: " + c);
                if (Cfg.AiSendDef && !string.IsNullOrEmpty(d)) sb.AppendLine("Определение: " + d.Replace("\r", " ").Replace("\n", " "));
            }
            sb.AppendLine("Ответь строго JSON: {\"descriptions\": {\"ИМЯ\": \"описание\", ...}}. Ключи — точно как в заголовках ###. Описание не длиннее " + maxLen + " символов.");
            prompts.Add(sb.ToString()); names.Add(part);
        }
        var cts = new CancellationTokenSource(); aiCts = cts; aiBusy = true; aiMessage = "ИИ: 0 из " + list.Count;
        bAi.Enabled = false; bCancel.Visible = true; UpdateInfo();
        string model = Cfg.AiModel.Trim();
        var th = new Thread(delegate()
        {
            int done = 0; string err = null;
            try
            {
                for (int b = 0; b < prompts.Count; b++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    string content = Ai.Chat(AiSystem, prompts[b], cts.Token);
                    var res = Ai.ParseDescriptions(content); var part = names[b]; done += part.Count; int cd = done;
                    InvokeSafe(delegate
                    {
                        int applied = 0;
                        foreach (var it in part)
                        {
                            string t; if (!res.TryGetValue(it.Nm, out t) && !res.TryGetValue(it.Full, out t)) continue;
                            t = t.Trim(); if (t.Length > maxLen) t = t.Substring(0, maxLen).TrimEnd() + "…";
                            if (Put(it, t, "ai?", model)) applied++;
                        }
                        aiMessage = "ИИ: " + cd + " из " + list.Count; RefreshAll();
                    });
                }
            }
            catch (OperationCanceledException) { err = "остановлено"; }
            catch (Exception ex) { err = ex.Message; }
            InvokeSafe(delegate
            {
                aiBusy = false; aiCts = null; bAi.Enabled = true; bCancel.Visible = false;
                aiMessage = err == null ? "ИИ: готово (" + list.Count + ")" : "ИИ: " + err;
                UpdateInfo();
                App.Log("ИИ-описания: " + aiMessage);
                if (err != null && err != "остановлено") UI.Warn(err);
            });
        });
        th.IsBackground = true; th.Start();
    }

    void InvokeSafe(Action a) { try { if (IsHandleCreated) BeginInvoke(a); else a(); } catch { } }

    // примеры: описания соседей с похожим именем (общий префикс)
    List<DescItem> Examples(DescItem like, int n)
    {
        var l = new List<DescItem>();
        foreach (var it in items) if (it != like && it.Cur.Length > 0) l.Add(it);
        string key = like.Nm.ToUpperInvariant();
        l.Sort(delegate(DescItem a, DescItem b) { return Common(b.Nm.ToUpperInvariant(), key) - Common(a.Nm.ToUpperInvariant(), key); });
        return l.Count > n ? l.GetRange(0, n) : l;
    }
    static int Common(string a, string b) { int i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++; return i; }

    public void MarkReviewed(List<DescItem> targets)
    {
        int n = 0;
        foreach (var it in targets)
            if (it.ShownSrc == "ai?") { if (it.New == null) { it.New = it.Cur; it.NewDetail = DescItem.Detail(it.CurStored); } it.NewSrc = "ai"; n++; }
        RefreshAll();
        if (n == 0) UI.Info("Среди выбранных нет описаний ИИ без проверки.");
    }

    // ---------- запись и откат ----------
    static string Stored(DescItem it)
    {
        string d = string.IsNullOrEmpty(it.NewDetail) ? "" : it.NewDetail;
        return it.NewSrc + "|" + d + "|" + DateTime.Now.ToString("yyyy-MM-dd");
    }

    public int Write()
    {
        var dirty = new List<DescItem>(); foreach (var it in items) if (it.Dirty) dirty.Add(it);
        if (dirty.Count == 0) { UI.Info("Нет изменений для записи."); return 0; }
        int local = 0; foreach (var it in dirty) if (it.Local) local++;
        if (Cfg.ConfirmApply && !UI.Ask("Записать описаний: " + dirty.Count + (local > 0 ? "\n(" + local + " из них хранятся локально в файле рядом с программой, а не в базе)" : "") + "?\n" + (db.D.Transactional ? "Всё выполняется одной транзакцией." : "При ошибке уже записанные описания будут возвращены."))) return 0;
        var h = new Hist { Title = DateTime.Now.ToString("HH:mm") + " — описаний: " + dirty.Count };
        var done = new List<DescItem>();
        try
        {
            Action run = delegate
            {
                foreach (var it in dirty)
                {
                    db.D.SetDescription(it.Sch, it.Nm, it.Tp, it.New, it.New.Length == 0 ? "" : Stored(it));
                    h.Items.Add(it); h.OldText.Add(it.Cur); h.OldStored.Add(it.CurStored); done.Add(it);
                }
            };
            if (db.D.Transactional) db.InTx(run); else run();
        }
        catch (Exception ex)
        {
            App.Log("ОШИБКА записи описаний: " + ex.Message);
            if (!db.D.Transactional) for (int i = done.Count - 1; i >= 0; i--) { try { db.D.SetDescription(done[i].Sch, done[i].Nm, done[i].Tp, h.OldText[i], h.OldStored[i]); } catch { } }
            UI.Warn("Ошибка при записи, изменения отменены:\n" + ex.Message); Reload(false); return 0;
        }
        hist.Add(h); App.Log("Записано описаний: " + dirty.Count);
        Reload(false); if (Changed != null) Changed();
        return dirty.Count;
    }

    public void Undo()
    {
        if (hist.Count == 0) { UI.Info("Откатывать нечего: в этой сессии описания не записывались."); return; }
        var h = hist[hist.Count - 1];
        if (DirtyCount() > 0 && !UI.Ask("Есть неприменённые изменения (" + DirtyCount() + "). Они будут отброшены. Продолжить?")) return;
        if (Cfg.ConfirmUndo && !UI.Ask("Откатить: " + h.Title + "?")) return;
        try
        {
            Action run = delegate { for (int i = 0; i < h.Items.Count; i++) db.D.SetDescription(h.Items[i].Sch, h.Items[i].Nm, h.Items[i].Tp, h.OldText[i], h.OldStored[i]); };
            if (db.D.Transactional) db.InTx(run); else run();
        }
        catch (Exception ex) { App.Log("ОШИБКА отката описаний: " + ex.Message); UI.Warn("Не удалось откатить:\n" + ex.Message); return; }
        hist.RemoveAt(hist.Count - 1); App.Log("Откат описаний: " + h.Title);
        Reload(false); if (Changed != null) Changed();
    }

    // ---------- экспорт ----------
    void ExportCsvDialog() { string p = Csv.Ask("descriptions"); if (p == null) return; try { UI.Info("Сохранено строк: " + ExportCsv(p) + "\n" + p); } catch (Exception ex) { UI.Warn(ex.Message); } }

    // «объект; описание; тип» — второй столбец совпадает с форматом импорта, файл можно править и загрузить обратно
    public int ExportCsv(string path)
    {
        var rows = new List<string[]>(); foreach (DataGridViewRow r in g.Rows) if (r.Visible) { var it = (DescItem)r.Tag; rows.Add(new[] { it.Full, it.Shown, DescItem.Kind(it.Tp) }); }
        Csv.WriteRows(new[] { "Объект", "Описание", "Тип" }, rows, path);
        return rows.Count;
    }

    void ExportSqlDialog()
    {
        using (var d = new SaveFileDialog())
        {
            d.Filter = "SQL-скрипт (*.sql)|*.sql"; d.FileName = "descriptions_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".sql"; d.InitialDirectory = App.Dir;
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try { UI.Info("В скрипте описаний: " + ExportSql(d.FileName) + "\n" + d.FileName); } catch (Exception ex) { UI.Warn(ex.Message); }
        }
    }

    // скрипт из черновика; если черновика нет — из всех текущих описаний
    public int ExportSql(string path)
    {
        var l = new List<DescItem>(); foreach (var it in items) if (it.Dirty) l.Add(it);
        bool draft = l.Count > 0; if (!draft) foreach (var it in items) if (it.Cur.Length > 0) l.Add(it);
        var sb = new StringBuilder();
        sb.AppendLine("-- LoginChanger: описания объектов (" + db.D.Title + ", " + db.Name + "), " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + (draft ? " — черновик" : " — текущие описания"));
        foreach (var it in l) sb.AppendLine(db.D.DescScript(it.Sch, it.Nm, it.Tp, draft ? it.New : it.Cur));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return l.Count;
    }
}

// ===== готовые настройки популярных ИИ-сервисов (все — OpenAI-совместимые) =====
class AiPreset
{
    public string Name, Url, KeyUrl, Note; public string[] Models; public bool Local;
    public AiPreset(string name, string url, string[] models, bool local, string keyUrl, string note) { Name = name; Url = url; Models = models; Local = local; KeyUrl = keyUrl; Note = note; }
}
static class AiPresets
{
    const string Cloud = "Названия моделей меняются: нажмите «Список моделей», чтобы получить актуальные прямо у сервиса.";
    public static readonly AiPreset[] All = {
        new AiPreset("Ollama (локально)", "http://localhost:11434/v1", new[] { "qwen2.5:14b", "qwen2.5:7b", "llama3.1:8b", "gemma2:9b", "mistral:7b" }, true, null,
            "Установите Ollama и скачайте модель, например: ollama pull qwen2.5:14b. Ключ не нужен. Данные не покидают компьютер."),
        new AiPreset("LM Studio (локально)", "http://localhost:1234/v1", new[] { "local-model" }, true, null,
            "В LM Studio загрузите модель и запустите локальный сервер (вкладка Developer). Ключ не нужен. Данные не покидают компьютер."),
        new AiPreset("llama.cpp / vLLM (локально)", "http://localhost:8080/v1", new[] { "local-model" }, true, null,
            "Адрес по умолчанию у llama.cpp server — 8080 (у vLLM обычно 8000). Ключ не нужен. Данные не покидают компьютер."),
        new AiPreset("OpenAI", "https://api.openai.com/v1", new[] { "gpt-4o-mini", "gpt-4o", "gpt-4.1-mini", "gpt-4.1" }, false, "https://platform.openai.com/api-keys", Cloud),
        new AiPreset("Anthropic Claude", "https://api.anthropic.com/v1", new[] { "claude-sonnet-5-5", "claude-haiku-5-5", "claude-opus-5-5" }, false, "https://console.anthropic.com/settings/keys",
            "Работает через слой совместимости Anthropic с OpenAI API. " + Cloud),
        new AiPreset("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", new[] { "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.0-flash" }, false, "https://aistudio.google.com/apikey",
            "Работает через слой совместимости Google с OpenAI API. " + Cloud),
        new AiPreset("OpenRouter (много моделей)", "https://openrouter.ai/api/v1", new[] { "openai/gpt-4o-mini", "google/gemini-2.5-flash", "meta-llama/llama-3.3-70b-instruct", "deepseek/deepseek-chat" }, false, "https://openrouter.ai/keys",
            "Один ключ для сотен моделей разных провайдеров. " + Cloud),
        new AiPreset("Groq (быстро)", "https://api.groq.com/openai/v1", new[] { "llama-3.3-70b-versatile", "llama-3.1-8b-instant" }, false, "https://console.groq.com/keys", Cloud),
        new AiPreset("DeepSeek", "https://api.deepseek.com/v1", new[] { "deepseek-chat" }, false, "https://platform.deepseek.com/api_keys", Cloud),
        new AiPreset("Mistral", "https://api.mistral.ai/v1", new[] { "mistral-small-latest", "mistral-large-latest" }, false, "https://console.mistral.ai/api-keys", Cloud),
        new AiPreset("xAI Grok", "https://api.x.ai/v1", new[] { "grok-3-mini", "grok-3" }, false, "https://console.x.ai", Cloud),
        new AiPreset("Together AI", "https://api.together.xyz/v1", new[] { "meta-llama/Llama-3.3-70B-Instruct-Turbo" }, false, "https://api.together.ai/settings/api-keys", Cloud),
    };
    static string N(string u) { return (u ?? "").Trim().TrimEnd('/').ToLowerInvariant(); }
    public static int IndexOfUrl(string url) { for (int i = 0; i < All.Length; i++) if (N(All[i].Url) == N(url)) return i; return -1; }
}
// ===== тема =====
static class Theme
{
    public static bool Dark;
    public static Color Bg, Fg, Muted, Input, Btn, Border, Accent, Sel, Header, Err, Pending, Denied, GridBack, Allow, NoneRow, AllRow;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

    public static void Init(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            Bg = Color.FromArgb(24, 28, 36); Fg = Color.FromArgb(230, 233, 240); Muted = Color.FromArgb(150, 158, 170);
            Input = Color.FromArgb(36, 42, 54); Btn = Color.FromArgb(48, 55, 70); Border = Color.FromArgb(70, 78, 95);
            Accent = Color.FromArgb(59, 110, 220); Sel = Color.FromArgb(45, 70, 115); Header = Color.FromArgb(38, 45, 58);
            Err = Color.FromArgb(255, 120, 120); Pending = Color.FromArgb(120, 100, 25); Denied = Color.FromArgb(115, 50, 55);
            GridBack = Color.FromArgb(30, 35, 45);
            Allow = Color.FromArgb(28, 82, 52); NoneRow = Color.FromArgb(88, 40, 46); AllRow = Color.FromArgb(40, 120, 76);
        }
        else
        {
            Bg = Color.FromArgb(244, 246, 250); Fg = Color.FromArgb(30, 35, 45); Muted = Color.FromArgb(110, 118, 130);
            Input = Color.White; Btn = Color.FromArgb(230, 234, 242); Border = Color.FromArgb(200, 206, 216);
            Accent = Color.FromArgb(37, 99, 235); Sel = Color.FromArgb(219, 234, 254); Header = Color.FromArgb(232, 237, 245);
            Err = Color.FromArgb(200, 40, 40); Pending = Color.FromArgb(255, 235, 130); Denied = Color.FromArgb(255, 200, 200);
            GridBack = Color.White;
            Allow = Color.FromArgb(205, 238, 215); NoneRow = Color.FromArgb(252, 226, 226); AllRow = Color.FromArgb(150, 214, 178);
        }
    }

    public static void Toggle()
    {
        Init(!Dark);
        Cfg.Theme = Dark ? "dark" : "light";
        var d = App.LoadIni(); d["theme"] = Cfg.Theme; App.SaveIni(d);
    }

    public static string Label { get { return Dark ? "Тема: тёмная" : "Тема: светлая"; } }

    static void TitleBar(Form f)
    {
        try
        {
            int v = Dark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
        }
        catch { }
    }

    public static void Apply(Control root)
    {
        var f = root as Form;
        if (f != null)
        {
            f.BackColor = Bg; f.ForeColor = Fg;
            if (f.IsHandleCreated) TitleBar(f); else f.HandleCreated += delegate { TitleBar(f); };
        }
        foreach (Control k in root.Controls) One(k);
    }

    public static void StyleButton(Button b)
    {
        string t = b.Tag as string;
        bool pr = t == "primary" || t == "tabsel";
        b.UseVisualStyleBackColor = false; b.FlatStyle = FlatStyle.Flat;
        b.BackColor = pr ? Accent : (t == "ghost" ? Bg : Btn); b.ForeColor = pr ? Color.White : Fg;
        b.FlatAppearance.BorderColor = pr ? Accent : Border;
        b.FlatAppearance.BorderSize = (t == "tab" || t == "tabsel") ? 0 : 1;
        b.FlatAppearance.MouseOverBackColor = pr ? ControlPaint.Light(Accent, 0.15f) : (t == "ghost" ? Btn : Border);
        b.Cursor = Cursors.Hand;
        if (b.MinimumSize.Height < 30) b.MinimumSize = new Size(0, 30);
    }

    static void One(Control k)
    {
        if (k is Button) StyleButton((Button)k);
        else if (k is TextBox)
        {
            k.BackColor = Input; k.ForeColor = Fg; ((TextBox)k).BorderStyle = BorderStyle.FixedSingle;
        }
        else if (k is ComboBox) { ((ComboBox)k).FlatStyle = FlatStyle.Flat; k.BackColor = Input; k.ForeColor = Fg; }
        else if (k is Label)
        {
            string t = k.Tag as string;
            k.ForeColor = t == "muted" ? Muted : t == "err" ? Err : Fg;
        }
        else if (k is NumericUpDown) { k.BackColor = Input; k.ForeColor = Fg; }
        else if (k is Panel && (k.Tag as string) == "line") k.BackColor = Border;
        else if (k is DataGridView) Grid((DataGridView)k);
        else if (k is SplitContainer) k.BackColor = Border;
        else
        {
            if (k is TabPage) ((TabPage)k).UseVisualStyleBackColor = false;
            k.BackColor = Bg; k.ForeColor = Fg;
        }
        if (!(k is DataGridView)) foreach (Control c in k.Controls) One(c);
    }

    static void Grid(DataGridView g)
    {
        g.BackgroundColor = GridBack; g.GridColor = Border; g.BorderStyle = BorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        foreach (var s in new[] { g.ColumnHeadersDefaultCellStyle, g.RowHeadersDefaultCellStyle })
        { s.BackColor = Header; s.ForeColor = Fg; s.SelectionBackColor = Header; s.SelectionForeColor = Fg; }
        g.DefaultCellStyle.BackColor = GridBack; g.DefaultCellStyle.ForeColor = Fg;
        g.DefaultCellStyle.SelectionBackColor = Sel; g.DefaultCellStyle.SelectionForeColor = Fg;
        g.RowTemplate.Height = 26;
    }
}

// ===== окно подключения =====
class ConnForm : Form
{
    ComboBox tServer = new ComboBox();
    TextBox tUser = new TextBox(), tPass = new TextBox(), tFile = new TextBox();
    FlatCombo cEng = new FlatCombo(), cAuth = new FlatCombo(), cDrv = new FlatCombo(), cDb = new FlatCombo();
    Button bConn = new Button(), bOk = new Button(), bTheme = new Button(), bFind = new Button();
    Label lMsg = new Label(), lDb, lEng, lSrv, lAuth, lUser, lPass, lDrv, lFile;
    System.Data.Common.DbConnection conn;
    public Db Result;
    Dictionary<string, string> ini;

    public ConnForm()
    {
        Text = "LoginChanger — подключение";
        AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 560); Size = new Size(540, 700);

        ini = App.LoadIni();
        cEng.Items.AddRange(new object[] { "SQL Server", "Firebird (ODBC)", "MySQL / MariaDB (ODBC)" });
        int ei; cEng.SelectedIndex = ini.ContainsKey("engine") && int.TryParse(ini["engine"], out ei) && ei >= 0 && ei <= 2 ? ei : 0;
        tServer.Text = ini.ContainsKey("server") ? ini["server"] : "localhost";
        tServer.DropDownStyle = ComboBoxStyle.DropDown;
        bFind.Text = "Найти"; bFind.Dock = DockStyle.Right; bFind.Width = 90;
        foreach (string s in LocalInstances()) AddServer(s);
        if (Cfg.RememberServers && ini.ContainsKey("servers"))
            foreach (string s in ini["servers"].Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries)) AddServer(s);
        cAuth.Items.AddRange(new object[] { "Windows-аутентификация", "SQL Server (sa и т.п.)" });
        cAuth.SelectedIndex = (ini.ContainsKey("auth") && ini["auth"] == "1") ? 1 : 0;
        tUser.Text = ini.ContainsKey("user") ? ini["user"] : "sa";
        tPass.UseSystemPasswordChar = true;
        tFile.Text = ini.ContainsKey("fbfile") ? ini["fbfile"] : "";
        bConn.Text = "Подключиться"; bConn.Tag = "primary"; bConn.AutoSize = true; bConn.MinimumSize = new Size(170, 36);
        bOk.Text = "Выбрать базу"; bOk.AutoSize = true; bOk.MinimumSize = new Size(170, 36); bOk.Enabled = false;
        lMsg.AutoSize = true; lMsg.MaximumSize = new Size(440, 0); lMsg.Margin = new Padding(3, 8, 3, 8);
        bTheme.Text = Theme.Label; bTheme.AutoSize = true; bTheme.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var bSet = new Button { Text = "Настройки", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };

        var title = new Label { Text = "LoginChanger", AutoSize = true, Font = new Font("Segoe UI Semibold", 20f), Margin = new Padding(3, 0, 3, 0) };
        var sub = new Label { Text = "Быстрые пользователи и права для баз данных", AutoSize = true, Tag = "muted", Margin = new Padding(3, 0, 3, 16) };

        var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(28, 44, 28, 20), AutoScroll = true };
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tl.RowCount = 2;
        tl.Controls.Add(title, 0, 0); tl.SetColumnSpan(title, 2);
        tl.Controls.Add(sub, 0, 1); tl.SetColumnSpan(sub, 2);
        lEng = AddRow(tl, "СУБД:", cEng);
        var pS = new Panel { Height = 32, MinimumSize = new Size(0, 32) };
        tServer.Dock = DockStyle.Fill; pS.Controls.Add(tServer); pS.Controls.Add(bFind);
        lSrv = AddRow(tl, "Сервер:", pS);
        lAuth = AddRow(tl, "Вход:", cAuth);
        lDrv = AddRow(tl, "Драйвер ODBC:", cDrv);
        lFile = AddRow(tl, "Файл базы:", tFile);
        lUser = AddRow(tl, "Логин:", tUser);
        lPass = AddRow(tl, "Пароль:", tPass);
        int r = tl.RowCount++; tl.Controls.Add(bConn, 1, r);
        r = tl.RowCount++; tl.Controls.Add(lMsg, 0, r); tl.SetColumnSpan(lMsg, 2);
        lDb = AddRow(tl, "База данных:", cDb);
        r = tl.RowCount++; tl.Controls.Add(bOk, 1, r);
        lDb.Visible = cDb.Visible = bOk.Visible = false;
        Controls.Add(tl); Controls.Add(bTheme); Controls.Add(bSet); bTheme.BringToFront(); bSet.BringToFront();
        Load += delegate { bTheme.Location = new Point(ClientSize.Width - bTheme.Width - 16, 14); bSet.Location = new Point(bTheme.Left - bSet.Width - 8, 14); };
        bSet.Click += delegate
        {
            using (var sf = new SettingsForm())
                if (sf.ShowDialog(this) == DialogResult.OK) { Theme.Init(Cfg.IsDark()); Theme.Apply(this); bTheme.Text = Theme.Label; }
        };

        cEng.SelectedIndexChanged += delegate { ApplyEngine(true); };
        cAuth.SelectedIndexChanged += delegate { tUser.Enabled = tPass.Enabled = cAuth.SelectedIndex == 1; Reset(null, null); };
        cDrv.SelectedIndexChanged += Reset;
        tServer.TextChanged += Reset; tUser.TextChanged += Reset; tPass.TextChanged += Reset; tFile.TextChanged += Reset;
        bConn.Click += Connect; bOk.Click += OpenDb; bFind.Click += Find;
        bTheme.Click += delegate { Theme.Toggle(); Theme.Apply(this); bTheme.Text = Theme.Label; Msg(lMsg.Text, lMsg.Tag as string == "err"); };
        AcceptButton = bConn;
        FormClosed += delegate { if (Result == null && conn != null) conn.Dispose(); };
        ApplyEngine(false);
        Theme.Apply(this);
    }

    static Label AddRow(TableLayoutPanel tl, string label, Control c)
    {
        int r = tl.RowCount++;
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) };
        tl.Controls.Add(l, 0, r);
        c.Dock = DockStyle.Fill; c.Margin = new Padding(3, 5, 3, 5); tl.Controls.Add(c, 1, r);
        return l;
    }

    int Eng { get { return cEng.SelectedIndex; } }   // 0 — SQL Server, 1 — Firebird, 2 — MySQL

    static List<string> OdbcDrivers(string pattern)
    {
        var l = new List<string>();
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers"))
                if (k != null)
                    foreach (string n in k.GetValueNames())
                        if (System.Text.RegularExpressions.Regex.IsMatch(n, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) l.Add(n);
        }
        catch { }
        l.Sort(delegate(string a, string b) { return (a.Contains("ANSI") ? 1 : 0) - (b.Contains("ANSI") ? 1 : 0); });
        return l;
    }

    void ApplyEngine(bool userChange)
    {
        bool ms = Eng == 0, fb = Eng == 1;
        lAuth.Visible = cAuth.Visible = ms;
        lDrv.Visible = cDrv.Visible = !ms;
        lFile.Visible = tFile.Visible = fb;
        bFind.Visible = ms;
        if (!ms)
        {
            string sel = cDrv.SelectedItem as string;
            cDrv.Items.Clear();
            foreach (string d in OdbcDrivers(fb ? "Firebird|InterBase" : "MySQL|MariaDB")) cDrv.Items.Add(d);
            if (cDrv.Items.Count > 0) cDrv.SelectedIndex = sel != null && cDrv.Items.Contains(sel) ? cDrv.Items.IndexOf(sel) : 0;
        }
        tUser.Enabled = tPass.Enabled = ms ? cAuth.SelectedIndex == 1 : true;
        if (userChange)
        {
            string u = tUser.Text.Trim().ToLower();
            if (u == "sa" || u == "root" || u == "sysdba" || u.Length == 0) tUser.Text = ms ? "sa" : fb ? "SYSDBA" : "root";
        }
        bOk.Text = fb ? "Открыть базу" : "Выбрать базу";
        Reset(null, null);
        if (!ms && cDrv.Items.Count == 0)
            Msg("ODBC-драйвер не найден. Установите драйвер " + (fb ? "Firebird ODBC" : "MySQL Connector/ODBC") + " (разрядность как у программы).", true);
    }

    void AddServer(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return;
        foreach (object o in tServer.Items) if (string.Equals((string)o, s, StringComparison.OrdinalIgnoreCase)) return;
        tServer.Items.Add(s);
    }

    // локальные экземпляры — из реестра (быстро), без сети
    static List<string> LocalInstances()
    {
        var l = new List<string>();
        foreach (RegistryView v in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, v))
                using (var k = b.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"))
                {
                    if (k == null) continue;
                    foreach (string n in k.GetValueNames())
                    {
                        string s = n.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "localhost" : "localhost\\" + n;
                        if (!l.Contains(s)) l.Add(s);
                    }
                }
            }
            catch { }
        }
        return l;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static List<string> NetServers()
    {
        var l = new List<string>();
        DataTable dt = SqlDataSourceEnumerator.Instance.GetDataSources();
        foreach (DataRow r in dt.Rows)
        {
            string n = Convert.ToString(r["ServerName"]), i = Convert.ToString(r["InstanceName"]);
            l.Add(i.Length > 0 ? n + "\\" + i : n);
        }
        return l;
    }

    // сетевой поиск: широковещательный запрос (нужна служба SQL Server Browser на серверах), идёт в фоне
    int findToken; bool searching;
    void Find(object s, EventArgs e)
    {
        if (searching) return;
        searching = true; int my = ++findToken;
        bFind.Enabled = false; bFind.Text = "Поиск…"; Msg("Ищем серверы в сети (до 8 секунд)…", false);
        foreach (string x in LocalInstances()) AddServer(x);
        var th = new Thread(delegate()
        {
            List<string> found = new List<string>();
            try { found = NetServers(); } catch { }
            try { BeginInvoke(new Action(delegate { FindDone(my, found, false); })); } catch { }
        });
        th.IsBackground = true; th.Start();
        var tm = new System.Windows.Forms.Timer { Interval = 8000 };
        tm.Tick += delegate { tm.Stop(); tm.Dispose(); FindDone(my, new List<string>(), true); };
        tm.Start();
    }

    void FindDone(int my, List<string> found, bool timeout)
    {
        foreach (string x in found) AddServer(x);
        if (my != findToken || !searching) return;
        searching = false; bFind.Enabled = true; bFind.Text = "Найти";
        int n = tServer.Items.Count;
        if (found.Count > 0) Msg("Найдено серверов в сети: " + found.Count + ". Выберите из списка.", false);
        else if (timeout) Msg("Сеть не ответила за 8 секунд. В списке локальные серверы (" + n + "); другой адрес введите вручную.", false);
        else Msg("В сети серверов не найдено (на них должна работать служба SQL Server Browser). Локальные: " + n + ".", false);
        if (n > 0) tServer.DroppedDown = true;
    }

    void Msg(string t, bool err)
    {
        lMsg.Text = t; lMsg.Tag = err ? "err" : null; lMsg.ForeColor = err ? Theme.Err : Theme.Fg;
    }

    void Reset(object s, EventArgs e)
    {
        if (!bOk.Visible) return;
        lDb.Visible = cDb.Visible = bOk.Visible = false; bOk.Enabled = false;
        if (conn != null) { conn.Dispose(); conn = null; }
        bConn.Text = "Подключиться"; bConn.Tag = "primary"; Theme.StyleButton(bConn);
        AcceptButton = bConn; Msg("", false);
    }

    // хост[:порт] -> (хост, порт)
    static void HostPort(string s, out string host, out string port)
    {
        s = s.Trim(); host = s; port = "";
        int i = s.LastIndexOf(':');
        if (i > 0 && s.IndexOf(':') == i) { host = s.Substring(0, i); port = s.Substring(i + 1); }
        if (host.Length == 0) host = "localhost";
    }

    string OdbcString()
    {
        string drv = cDrv.SelectedItem as string;
        if (drv == null) throw new Exception("Не выбран ODBC-драйвер.");
        string host, port; HostPort(tServer.Text, out host, out port);
        string u = tUser.Text.Trim(), p = tPass.Text;
        if (Eng == 1)
        {
            string file = tFile.Text.Trim();
            if (file.Length == 0) throw new Exception("Укажите путь к файлу базы (.fdb) на сервере.");
            string dsn = host + (port.Length > 0 ? "/" + port : "") + ":" + file;
            return "DRIVER={" + drv + "};DBNAME=" + dsn + ";UID=" + u + ";PWD=" + p + ";CHARSET=UTF8;";
        }
        return "DRIVER={" + drv + "};SERVER=" + host + ";PORT=" + (port.Length > 0 ? port : "3306") + ";UID=" + u + ";PWD=" + p + ";";
    }

    static string Scalar(System.Data.Common.DbConnection c, string sql)
    {
        using (var cmd = c.CreateCommand()) { cmd.CommandText = sql; object o = cmd.ExecuteScalar(); return o == null || o is DBNull ? "" : Convert.ToString(o); }
    }

    void Connect(object s, EventArgs e)
    {
        try
        {
            Cursor = Cursors.WaitCursor; Msg("", false);
            if (conn != null) conn.Dispose();
            cDb.Items.Clear();
            bool sa = true;
            if (Eng == 0)
            {
                var sb = new SqlConnectionStringBuilder();
                sb.DataSource = tServer.Text.Trim(); sb.InitialCatalog = "master";
                sb.TrustServerCertificate = Cfg.TrustCert; sb.Encrypt = Cfg.Encrypt; sb.ConnectTimeout = Cfg.ConnTimeout; sb.ApplicationName = "LoginChanger";
                if (cAuth.SelectedIndex == 0) sb.IntegratedSecurity = true;
                else { sb.UserID = tUser.Text.Trim(); sb.Password = tPass.Text; }
                conn = new SqlConnection(sb.ConnectionString);
                conn.Open();
                using (var c = conn.CreateCommand())
                {
                    c.CommandText = "SELECT name FROM sys.databases WHERE state=0 AND HAS_DBACCESS(name)=1 AND name NOT IN ('master','tempdb','model','msdb') ORDER BY name";
                    using (var rd = c.ExecuteReader()) while (rd.Read()) cDb.Items.Add(rd.GetString(0));
                }
                sa = Scalar(conn, "SELECT IS_SRVROLEMEMBER('sysadmin')") == "1";
            }
            else
            {
                var oc = new System.Data.Odbc.OdbcConnection(OdbcString());
                conn = oc; oc.ConnectionTimeout = Cfg.ConnTimeout; oc.Open();
                if (Eng == 2)
                {
                    using (var c = conn.CreateCommand())
                    {
                        c.CommandText = "SHOW DATABASES";
                        using (var rd = c.ExecuteReader())
                            while (rd.Read())
                            {
                                string n = Convert.ToString(rd.GetValue(0));
                                if (n != "information_schema" && n != "mysql" && n != "performance_schema" && n != "sys") cDb.Items.Add(n);
                            }
                    }
                }
            }
            var ini2 = App.LoadIni();
            if (ini2.ContainsKey("db") && cDb.Items.Contains(ini2["db"])) cDb.SelectedItem = ini2["db"];
            else if (cDb.Items.Count > 0) cDb.SelectedIndex = 0;
            App.Log("Подключено к " + tServer.Text.Trim() + " (" + cEng.SelectedItem + ")");
            if (Eng != 1 && cDb.Items.Count == 0) { Msg("Подключено, но доступных баз нет.", true); return; }
            Msg(Eng == 1 ? "Подключение успешно. Откройте базу." : sa ? "Подключение успешно. Выберите базу." : "Подключено, но НЕ sysadmin: часть действий может не работать.", !sa);
            lDb.Visible = cDb.Visible = Eng != 1; bOk.Visible = true; bOk.Enabled = true;
            bConn.Text = "Переподключиться"; bConn.Tag = null; Theme.StyleButton(bConn);
            bOk.Tag = "primary"; Theme.StyleButton(bOk);
            AcceptButton = bOk; if (Eng != 1) cDb.Focus();
        }
        catch (Exception ex)
        {
            Msg(ex.Message, true);
            lDb.Visible = cDb.Visible = bOk.Visible = false; bOk.Enabled = false;
        }
        finally { Cursor = Cursors.Default; }
    }

    void OpenDb(object s, EventArgs e)
    {
        try
        {
            var d = new Db { C = conn, Server = tServer.Text.Trim() };
            if (Eng == 0)
            {
                d.Kind = "mssql"; d.D = new MssqlDialect(); d.Name = (string)cDb.SelectedItem;
                conn.ChangeDatabase(d.Name);
                d.Who = Scalar(conn, "SELECT SUSER_SNAME()");
            }
            else if (Eng == 1)
            {
                d.Kind = "firebird"; d.D = new FirebirdDialect();
                string f = tFile.Text.Trim(); int i = Math.Max(f.LastIndexOf('\\'), f.LastIndexOf('/'));
                d.Name = i >= 0 ? f.Substring(i + 1) : f;
                d.Who = Scalar(conn, "SELECT CURRENT_USER FROM RDB$DATABASE").Trim();
            }
            else
            {
                d.Kind = "mysql"; d.D = new MySqlDialect(); d.Name = (string)cDb.SelectedItem;
                using (var c = conn.CreateCommand()) { c.CommandText = "USE " + d.D.Q(d.Name); c.ExecuteNonQuery(); }
                d.Who = Scalar(conn, "SELECT CURRENT_USER()");
            }
            d.D.Bind(d);
            ini = App.LoadIni();
            ini["engine"] = Eng.ToString(); ini["server"] = d.Server; ini["auth"] = cAuth.SelectedIndex.ToString(); ini["user"] = tUser.Text.Trim();
            if (Eng == 0 || Eng == 2) ini["db"] = d.Name;
            if (Eng == 1) ini["fbfile"] = tFile.Text.Trim();
            var sv = new List<string>(); foreach (object o in tServer.Items) sv.Add((string)o);
            ini["servers"] = Cfg.RememberServers ? string.Join(";", sv.ToArray()) : "";
            App.SaveIni(ini);
            App.Log("Открыта БД " + d.Name + " (" + d.D.Title + ") как " + d.Who);
            Result = d; DialogResult = DialogResult.OK;
        }
        catch (Exception ex) { Msg(ex.Message, true); }
    }
}

// ===== поле пароля с генератором =====
class PwdBox : Panel
{
    public TextBox T = new TextBox();
    public PwdBox()
    {
        Height = 36; MinimumSize = new Size(0, 36); Dock = DockStyle.Fill;
        T.Dock = DockStyle.Fill; T.Font = new Font("Consolas", 12f); T.Text = App.GenPwd(Cfg.PwdLen);
        var g = new Button { Text = "Новый", Dock = DockStyle.Right, Width = 90 };
        var c = new Button { Text = "Копировать", Dock = DockStyle.Right, Width = 110 };
        g.Click += delegate { T.Text = App.GenPwd(Cfg.PwdLen); };
        c.Click += delegate { try { Clipboard.SetText(T.Text); } catch { } };
        Controls.Add(T); Controls.Add(g); Controls.Add(c);
    }
}
class PwdDialog : Form
{
    PwdBox pb = new PwdBox();
    public string Pwd;
    public PwdDialog(string title, string info)
    {
        Text = title; AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; Size = new Size(540, 190);
        var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(20) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cn = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var fl = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        fl.Controls.Add(cn); fl.Controls.Add(ok);
        tl.Controls.Add(new Label { Text = info, AutoSize = true }, 0, 0);
        tl.Controls.Add(pb, 0, 1); tl.Controls.Add(fl, 0, 2);
        Controls.Add(tl); AcceptButton = ok; CancelButton = cn;
        ok.Click += delegate { Pwd = pb.T.Text; };
        ok.Tag = "primary"; Theme.Apply(this);
    }
}

// ===== создание пользователя =====
class CreateForm : Form
{
    public TextBox TName = new TextBox();
    public PwdBox Pwd = new PwdBox();
    public ComboBox Tpl = new ComboBox();
    public CheckBox PolicyOff = new CheckBox();
    public Label HintL;

    public CreateForm(string preset, List<string> tplItems)
    {
        Text = "Создать пользователя"; AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; Size = new Size(560, 300);
        TName.Text = preset ?? "";
        Tpl.DropDownStyle = ComboBoxStyle.DropDownList;
        Tpl.Items.Add("(без прав)");
        foreach (string s in tplItems) Tpl.Items.Add(s);
        Tpl.SelectedIndex = 0;
        PolicyOff.Text = "Не проверять сложность пароля сервером"; PolicyOff.AutoSize = true; PolicyOff.Checked = Cfg.PolicyOff;

        var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(20) };
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Row(tl, "Имя логина:", TName);
        Row(tl, "Пароль:", Pwd);
        Row(tl, "Права по шаблону:", Tpl);
        tl.Controls.Add(PolicyOff, 1, 3);
        var hint = HintL = new Label { Text = "Имя вида DOMAIN\\user — логин Windows (пароль не нужен). Если логин уже есть на сервере — пароль не меняется.", AutoSize = true, MaximumSize = new Size(480, 0), Tag = "muted" };
        tl.Controls.Add(hint, 1, 4);
        var ok = new Button { Text = "Создать", DialogResult = DialogResult.OK, AutoSize = true };
        var cn = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var fl = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        fl.Controls.Add(cn); fl.Controls.Add(ok);
        tl.Controls.Add(fl, 1, 5);
        Controls.Add(tl); AcceptButton = ok; CancelButton = cn;
        TName.TextChanged += delegate { Pwd.Enabled = TName.Text.IndexOf('\\') < 0; };
        Pwd.Enabled = TName.Text.IndexOf('\\') < 0;
        ok.Tag = "primary"; Theme.Apply(this);
    }
    static void Row(TableLayoutPanel tl, string label, Control c)
    {
        int r = tl.RowCount; tl.RowCount++;
        tl.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) }, 0, r);
        c.Dock = DockStyle.Fill; c.Margin = new Padding(3, 5, 3, 5); tl.Controls.Add(c, 1, r);
    }
}

// ===== текстовое поле с серой подсказкой, пока оно пустое =====
class HintBox : TextBox
{
    public string Hint = "";
    bool empty = true;
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F && m.Msg != 0x0317 && m.Msg != 0x0318) return;
        if (Hint.Length == 0 || !empty || Focused) return;
        if (m.Msg == 0x000F)   // WM_PAINT
            using (var g = CreateGraphics()) Draw(g);
        else if (m.Msg == 0x0317 || m.Msg == 0x0318)   // WM_PRINT / WM_PRINTCLIENT (снимок окна)
            using (var g = Graphics.FromHdc(m.WParam)) Draw(g);
    }
    void Draw(Graphics g)
    {
        TextRenderer.DrawText(g, Hint, Font, new Rectangle(1, 1, Width - 2, Height - 2), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); empty = TextLength == 0; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

// (устаревший вариант через EM_SETCUEBANNER, не используется)
static class Cue
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, string l);
    public static void Set(TextBox t, string s)
    {
        Action a = delegate { try { SendMessage(t.Handle, 0x1501, (IntPtr)1, s); } catch { } };
        if (t.IsHandleCreated) a(); else t.HandleCreated += delegate { a(); };
    }
}

// ===== выпадающий список в стиле темы (ровная высота, цвета темы в раскрытом списке) =====
class FlatCombo : ComboBox
{
    public FlatCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList; DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 22; FlatStyle = FlatStyle.Flat;
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var b = new SolidBrush(sel ? Theme.Sel : Theme.Input)) e.Graphics.FillRectangle(b, e.Bounds);
        if (e.Index < 0) return;
        TextRenderer.DrawText(e.Graphics, Convert.ToString(Items[e.Index]), Font, new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height),
            Theme.Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

// ===== крупный чекбокс (одинаковый в таблице и в списках) =====
static class Box
{
    public static void Draw(Graphics g, Rectangle r, bool on, bool hover, bool partial = false)
    {
        var old = g.SmoothingMode; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var p = new System.Drawing.Drawing2D.GraphicsPath())
        {
            int d = 6;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            using (var b = new SolidBrush(on ? Theme.Accent : Theme.Input)) g.FillPath(b, p);
            using (var pen = new Pen(on || partial ? Theme.Accent : (hover ? Theme.Accent : Theme.Muted), 2f)) g.DrawPath(pen, p);
        }
        if (partial && !on)
            using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X + r.Width / 2 - 5, r.Y + r.Height / 2 - 2, 10, 4);
        if (on)
            using (var pen = new Pen(Color.White, 2.6f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            {
                float s = r.Width / 22f;
                g.DrawLines(pen, new[] { new PointF(r.X + 5 * s, r.Y + 11.5f * s), new PointF(r.X + 9.5f * s, r.Y + 16 * s), new PointF(r.X + 17 * s, r.Y + 6.5f * s) });
            }
        g.SmoothingMode = old;
    }
}

// ===== выпадающий список с галочками (множественный выбор + поиск) =====
// Ничего не отмечено = фильтра нет (показываем всё).
class CheckPicker : Button
{
    string caption;
    List<string> items = new List<string>();
    public HashSet<string> Checked = new HashSet<string>();
    public event Action Changed;
    DateTime closedAt = DateTime.MinValue;

    public CheckPicker(string caption)
    {
        this.caption = caption;
        AutoSize = true; MinimumSize = new Size(170, 30); TextAlign = ContentAlignment.MiddleLeft;
        UpdateText();
        Click += Open;
    }

    public void SetItems(IEnumerable<string> src)
    {
        items = new List<string>(src);
        Checked.IntersectWith(items);
        UpdateText();
    }

    void UpdateText() { Text = caption + ": " + (Checked.Count == 0 ? "все" : "выбрано " + Checked.Count) + "  ▾"; }
    void Fire() { UpdateText(); if (Changed != null) Changed(); }

    void Open(object s, EventArgs e)
    {
        if ((DateTime.Now - closedAt).TotalMilliseconds < 300) return;   // клик по кнопке закрыл окно — не открывать заново
        var f = new Form();
        f.FormBorderStyle = FormBorderStyle.None; f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual;
        f.KeyPreview = true; f.Font = new Font("Segoe UI", 10f); f.Padding = new Padding(1);
        f.Location = PointToScreen(new Point(0, Height)); f.Size = new Size(Math.Max(Width, 340), 420);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var tb = new TextBox { Dock = DockStyle.Top };
        var spacer = new Panel { Dock = DockStyle.Top, Height = 8 };
        var lb = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 32, IntegralHeight = false, BorderStyle = BorderStyle.None };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var bAll = new Button { Text = "Отметить найденные", AutoSize = true };
        var bClr = new Button { Text = "Сбросить", AutoSize = true };
        var bOk = new Button { Text = "Готово", AutoSize = true, Tag = "primary" };
        bar.Controls.Add(bAll); bar.Controls.Add(bClr); bar.Controls.Add(bOk);
        host.Controls.Add(lb); host.Controls.Add(spacer); host.Controls.Add(tb); host.Controls.Add(bar);
        f.Controls.Add(host);

        Action rebuild = delegate
        {
            lb.BeginUpdate(); lb.Items.Clear();
            string q = tb.Text.Trim();
            foreach (string it in items)
                if (q.Length == 0 || it.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) lb.Items.Add(it);
            lb.EndUpdate();
        };
        Action<int> toggle = delegate(int i)
        {
            if (i < 0 || i >= lb.Items.Count) return;
            string it = (string)lb.Items[i];
            if (!Checked.Remove(it)) Checked.Add(it);
            lb.Invalidate(lb.GetItemRectangle(i)); Fire();
        };
        lb.DrawItem += delegate(object o, DrawItemEventArgs a)
        {
            if (a.Index < 0) return;
            string it = (string)lb.Items[a.Index];
            bool sel = (a.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Sel : Theme.Bg)) a.Graphics.FillRectangle(b, a.Bounds);
            int sz = 22;
            Box.Draw(a.Graphics, new Rectangle(a.Bounds.X + 6, a.Bounds.Y + (a.Bounds.Height - sz) / 2, sz, sz), Checked.Contains(it), false);
            TextRenderer.DrawText(a.Graphics, it, lb.Font, new Rectangle(a.Bounds.X + 38, a.Bounds.Y, a.Bounds.Width - 40, a.Bounds.Height), Theme.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        };
        lb.MouseDown += delegate(object o, MouseEventArgs a) { if (a.Button == MouseButtons.Left) toggle(lb.IndexFromPoint(a.Location)); };
        lb.KeyDown += delegate(object o, KeyEventArgs a) { if (a.KeyCode == Keys.Space) { toggle(lb.SelectedIndex); a.Handled = true; } };
        tb.TextChanged += delegate { rebuild(); };
        bAll.Click += delegate { foreach (object o in lb.Items) Checked.Add((string)o); lb.Invalidate(); Fire(); };
        bClr.Click += delegate { Checked.Clear(); lb.Invalidate(); Fire(); };
        bOk.Click += delegate { f.Close(); };
        f.Deactivate += delegate { if (!f.IsDisposed) f.Close(); };
        f.KeyDown += delegate(object o, KeyEventArgs a) { if (a.KeyCode == Keys.Escape) f.Close(); };
        f.FormClosed += delegate { closedAt = DateTime.Now; f.Dispose(); };
        f.Shown += delegate { tb.Focus(); };

        Theme.Apply(f); f.BackColor = Theme.Border;     // рамка 1px
        lb.BackColor = Theme.Bg;
        rebuild();
        f.Show(FindForm());
    }
}

// ===== история (для отката), CSV, диалоги копирования и массового создания =====
class UndoItem { public string Db, Title; public List<string> Sql = new List<string>(); }
static class History { public static List<UndoItem> Stack = new List<UndoItem>(); }

class CreateResult { public bool Ok; public string Msg; public bool NewLogin; }

static class Csv
{
    public static Encoding Enc()
    {
        switch (Cfg.CsvEnc) { case "utf8": return new UTF8Encoding(false); case "cp1251": return Encoding.GetEncoding(1251); default: return new UTF8Encoding(true); }
    }
    static string Cell(object v)
    {
        if (v is bool) return (bool)v ? Cfg.CsvMark : "";
        string s = v == null ? "" : Convert.ToString(v);
        s = s.Replace("\r", " ").Replace("\n", " ");
        if (s.IndexOfAny(new[] { Cfg.CsvDelim[0], '"', '\r', '\n' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
    public static string Line(IEnumerable<string> cells)
    {
        var l = new List<string>();
        foreach (string c in cells) l.Add(Cell(c));
        return string.Join(Cfg.CsvDelim, l.ToArray());
    }
    // только видимые строки и столбцы (учитывает фильтры), разделитель «;» и BOM — открывается в Excel
    public static int Write(DataGridView g, string path)
    {
        var cols = new List<DataGridViewColumn>();
        foreach (DataGridViewColumn c in g.Columns) if (c.Visible && (c.Tag as string) != "nocsv") cols.Add(c);
        var sb = new StringBuilder();
        var h = new List<string>(); foreach (var c in cols) h.Add(c.HeaderText);
        sb.AppendLine(Line(h));
        int n = 0;
        foreach (DataGridViewRow r in g.Rows)
        {
            if (!r.Visible) continue;
            var cells = new List<object>();
            var one = new List<string>();
            foreach (var c in cols) one.Add(Cell(r.Cells[c.Index].Value).Replace("\u0001", ""));
            sb.AppendLine(string.Join(Cfg.CsvDelim, one.ToArray())); n++;
        }
        File.WriteAllText(path, sb.ToString(), Enc());
        return n;
    }
    public static void WriteRows(string[] header, List<string[]> rows, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Line(header));
        foreach (string[] r in rows) sb.AppendLine(Line(r));
        File.WriteAllText(path, sb.ToString(), Enc());
    }
    public static string Ask(string name)
    {
        using (var d = new SaveFileDialog())
        {
            d.Filter = "CSV (открывается в Excel)|*.csv"; d.InitialDirectory = App.Dir;
            d.FileName = name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv";
            return d.ShowDialog() == DialogResult.OK ? d.FileName : null;
        }
    }
    public static void Export(DataGridView g, string name)
    {
        string p = Ask(name); if (p == null) return;
        try { int n = Write(g, p); App.Log("Экспорт CSV: " + p + " (" + n + " строк)"); UI.Info("Сохранено строк: " + n + "\n" + p); }
        catch (Exception ex) { UI.Warn(ex.Message); }
    }
}

class CopyForm : Form
{
    public ComboBox Src = new ComboBox(), Dst = new ComboBox(), Mode = new ComboBox();
    public CopyForm(string title, string note, List<string> src, List<string> dst, bool withMode)
    {
        Text = title; AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        foreach (ComboBox c in new[] { Src, Dst, Mode }) c.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (string s in src) Src.Items.Add(s);
        foreach (string s in dst) Dst.Items.Add(s);
        Mode.Items.Add("Заменить: сделать точно как у источника"); Mode.Items.Add("Добавить: только недостающие права");
        if (Src.Items.Count > 0) Src.SelectedIndex = 0;
        if (Dst.Items.Count > 0) Dst.SelectedIndex = 0;
        Mode.SelectedIndex = 1;

        var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(20), AutoSize = true, MinimumSize = new Size(520, 0) };
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var n = new Label { Text = note, AutoSize = true, MaximumSize = new Size(470, 0), Tag = "muted", Margin = new Padding(3, 0, 3, 14) };
        tl.Controls.Add(n, 0, 0); tl.SetColumnSpan(n, 2);
        int r = 1;
        Row(tl, ref r, "Взять права у:", Src); Row(tl, ref r, "Кому:", Dst);
        if (withMode) Row(tl, ref r, "Режим:", Mode);
        var ok = new Button { Text = "Продолжить", DialogResult = DialogResult.OK, AutoSize = true, Tag = "primary" };
        var cn = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        var fl = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        fl.Controls.Add(cn); fl.Controls.Add(ok);
        tl.Controls.Add(fl, 0, r); tl.SetColumnSpan(fl, 2);
        Controls.Add(tl); AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        AcceptButton = ok; CancelButton = cn;
        Theme.Apply(this);
    }
    static void Row(TableLayoutPanel tl, ref int r, string label, Control c)
    {
        tl.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) }, 0, r);
        c.Dock = DockStyle.Fill; c.Margin = new Padding(3, 5, 3, 5); tl.Controls.Add(c, 1, r); r++;
    }
}

class BulkForm : Form
{
    Func<string, string, bool, string, CreateResult> worker;
    TextBox tNames = new TextBox();
    ComboBox cTpl = new ComboBox();
    CheckBox chPol = new CheckBox();
    DataGridView gr = new DataGridView();
    Panel pIn = new Panel(), pOut = new Panel();
    public List<string[]> Rows = new List<string[]>();

    public BulkForm(List<string> tpls, Func<string, string, bool, string, CreateResult> worker)
    {
        this.worker = worker;
        Text = "Массовое создание пользователей"; AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(560, 440); Size = new Size(700, 560);
        ShowInTaskbar = false;

        // --- ввод ---
        var tl = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(20) };
        tl.RowStyles.Add(new RowStyle(SizeType.AutoSize)); tl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) tl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tNames.Multiline = true; tNames.ScrollBars = ScrollBars.Vertical; tNames.Dock = DockStyle.Fill; tNames.Font = new Font("Consolas", 11f);
        tNames.AcceptsReturn = true; tNames.Margin = new Padding(3, 3, 3, 12);
        cTpl.DropDownStyle = ComboBoxStyle.DropDownList; cTpl.Dock = DockStyle.Fill; cTpl.Margin = new Padding(3, 3, 3, 8);
        cTpl.Items.Add("(без прав)"); foreach (string s in tpls) cTpl.Items.Add(s); cTpl.SelectedIndex = 0;
        chPol.Text = "Не проверять сложность пароля сервером"; chPol.AutoSize = true; chPol.Margin = new Padding(3, 3, 3, 12);
        var go = new Button { Text = "Создать всех", AutoSize = true, Tag = "primary" };
        var cn = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var fl = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        fl.Controls.Add(cn); fl.Controls.Add(go);
        tl.Controls.Add(new Label { Text = "Логины — по одному в строке (DOMAIN\\user — пользователь Windows):", AutoSize = true, Margin = new Padding(3, 0, 3, 6) }, 0, 0);
        tl.Controls.Add(tNames, 0, 1);
        tl.Controls.Add(new Label { Text = "Права по шаблону:", AutoSize = true, Margin = new Padding(3, 0, 3, 4) }, 0, 2);
        tl.Controls.Add(cTpl, 0, 3); tl.Controls.Add(chPol, 0, 4); tl.Controls.Add(fl, 0, 5);
        tl.Dock = DockStyle.Fill; pIn.Dock = DockStyle.Fill; pIn.Controls.Add(tl);

        // --- результат ---
        var t2 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(20) };
        t2.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t2.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t2.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var note = new Label { Text = "Пароли показаны один раз — сохраните их (копировать или в CSV).", AutoSize = true, Tag = "muted", Margin = new Padding(3, 0, 3, 8) };
        gr.Dock = DockStyle.Fill; gr.ReadOnly = true; gr.AllowUserToAddRows = false; gr.RowHeadersVisible = false;
        gr.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; gr.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        gr.Columns.Add("l", "Логин"); gr.Columns.Add("p", "Пароль"); gr.Columns.Add("s", "Результат");
        gr.Columns[1].DefaultCellStyle.Font = new Font("Consolas", 11f); gr.Columns[1].FillWeight = 40; gr.Columns[2].FillWeight = 80;
        gr.Margin = new Padding(3, 3, 3, 12);
        var bCopy = new Button { Text = "Копировать всё", AutoSize = true };
        var bCsv = new Button { Text = "Сохранить в CSV", AutoSize = true };
        var bClose = new Button { Text = "Закрыть", AutoSize = true, Tag = "primary", DialogResult = DialogResult.OK };
        var fl2 = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        fl2.Controls.Add(bClose); fl2.Controls.Add(bCsv); fl2.Controls.Add(bCopy);
        t2.Controls.Add(note, 0, 0); t2.Controls.Add(gr, 0, 1); t2.Controls.Add(fl2, 0, 2);
        pOut.Dock = DockStyle.Fill; pOut.Controls.Add(t2); pOut.Visible = false;

        Controls.Add(pOut); Controls.Add(pIn);
        CancelButton = cn;
        go.Click += delegate
        {
            var names = Parse(tNames.Text);
            if (names.Count == 0) { UI.Warn("Введите хотя бы один логин."); return; }
            if (!UI.Ask("Создать пользователей: " + names.Count + "?")) return;
            Cursor = Cursors.WaitCursor;
            try { Process(names, cTpl.SelectedIndex > 0 ? (string)cTpl.SelectedItem : null, chPol.Checked); }
            finally { Cursor = Cursors.Default; }
        };
        bCopy.Click += delegate
        {
            var sb = new StringBuilder();
            foreach (string[] r in Rows) sb.AppendLine(r[0] + "\t" + r[1] + "\t" + r[2]);
            try { Clipboard.SetText(sb.ToString()); UI.Info("Скопировано."); } catch { }
        };
        bCsv.Click += delegate
        {
            string p = Csv.Ask("users"); if (p == null) return;
            try { Csv.WriteRows(new[] { "Логин", "Пароль", "Результат" }, Rows, p); UI.Info("Сохранено:\n" + p); }
            catch (Exception ex) { UI.Warn(ex.Message); }
        };
        Theme.Apply(this);
    }

    public static List<string> Parse(string text)
    {
        var l = new List<string>();
        foreach (string s in text.Split(new[] { '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string n = s.Trim();
            if (n.Length == 0) continue;
            bool dup = false; foreach (string x in l) if (string.Equals(x, n, StringComparison.OrdinalIgnoreCase)) dup = true;
            if (!dup) l.Add(n);
        }
        return l;
    }

    public void Process(List<string> names, string tpl, bool off)
    {
        Rows.Clear(); gr.Rows.Clear();
        foreach (string n in names)
        {
            bool win = n.IndexOf('\\') >= 0;
            string pwd = win ? "" : App.GenPwd(Cfg.PwdLen);
            CreateResult r = worker(n, pwd, off, tpl);
            string shown = r.NewLogin ? (win ? "(Windows)" : pwd) : "—";
            string status = (r.Ok ? "OK: " : "Ошибка: ") + r.Msg;
            Rows.Add(new[] { n, shown, status });
            int i = gr.Rows.Add(n, shown, status);
            if (!r.Ok) gr.Rows[i].DefaultCellStyle.ForeColor = Theme.Err;
        }
        pIn.Visible = false; pOut.Visible = true;
        AcceptButton = null; CancelButton = null;
    }
}

// ===== вкладка "Пользователи" =====
class UsersTab : Panel
{
    Db db;
    DataGridView g = new DataGridView();
    public event Action Changed;

    class Item { public string Kind, Name, Login, LType, SidHex; public bool Orphan, Prot; }

    public UsersTab(Db db)
    {
        this.db = db;
        g.Dock = DockStyle.Fill; g.ReadOnly = true; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = false; g.RowHeadersVisible = false;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; g.BackgroundColor = SystemColors.Window;
        g.Columns.Add("u", "Пользователь БД / логин"); g.Columns.Add("l", "Логин сервера");
        g.Columns.Add("t", "Тип"); g.Columns.Add("s", "Статус");

        var bar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 8) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = FlowRow(); left.Dock = DockStyle.Fill; var right = FlowRow(); right.WrapContents = false; right.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        AddBtn(left, "Создать пользователя", delegate { Create(null); }, "primary");
        AddBtn(left, "Массовое создание", delegate { BulkCreate(); }, "ghost");
        bAddLogin = AddBtn(left, "Добавить логин в БД", delegate { var it = Sel(); if (it == null || it.Kind != "login") { UI.Warn("Выберите строку «Логин есть, в БД нет»."); return; } Create(it.Name); }, "ghost");
        bOrph = AddBtn(left, "Починить сироту", delegate { FixOrphan(); }, "ghost");
        AddBtn(left, "Сменить пароль", delegate { ChangePwd(); }, "ghost");
        AddBtn(left, "Права как у…", delegate { CopyFrom(); }, "ghost");
        AddBtn(left, "Сохранить как шаблон", delegate { SaveTpl(); }, "ghost");
        tfind.Width = 200; tfind.Margin = new Padding(0, 4, 8, 3); tfind.Hint = "Поиск пользователей…";
        tfind.TextChanged += delegate { Filter(); }; right.Controls.Add(tfind);
        pick.Margin = new Padding(0, 3, 8, 3); pick.Changed += Filter; right.Controls.Add(pick);
        AddBtn(right, "Экспорт CSV", delegate { Csv.Export(g, "users"); }, "ghost");
        AddBtn(right, "Обновить", delegate { Reload(); }, "ghost").Margin = new Padding(0, 3, 0, 3);
        if (db.Kind != "mssql") { bAddLogin.Visible = false; bOrph.Visible = false; }
        bar.Controls.Add(left, 0, 0); bar.Controls.Add(right, 1, 0);        var legend = new Label { Text = db.Kind == "mssql" ? "Администраторы (sysadmin, db_owner), sa и dbo скрыты и не меняются.  Оранжевые — логины сервера без доступа к этой БД." : "Администраторы скрыты и не меняются. Пользователи общие для всего сервера; права показаны для " + (db.Kind == "mysql" ? "базы" : "базы") + " «" + db.Name + "».", AutoSize = true, Tag = "muted", Dock = DockStyle.Top, Margin = new Padding(0), Padding = new Padding(0, 0, 0, 8) };
        Controls.Add(g); Controls.Add(legend); Controls.Add(bar);
    }

    HintBox tfind = new HintBox();
    Button bAddLogin, bOrph;
    CheckPicker pick = new CheckPicker("Список");
    void Filter()
    {
        var w = tfind.Text.ToLower().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        g.CurrentCell = null;
        foreach (DataGridViewRow r in g.Rows)
        {
            string s = (r.Cells[0].Value + " " + r.Cells[1].Value + " " + r.Cells[2].Value + " " + r.Cells[3].Value).ToLower();
            bool ok = true;
            foreach (string x in w) if (!s.Contains(x)) ok = false;
            if (ok && pick.Checked.Count > 0 && !pick.Checked.Contains((string)r.Cells[0].Value)) ok = false;
            r.Visible = ok;
        }
    }

    static Button AddBtn(Control p, string t, EventHandler h, string tag = null)
    {
        var b = new Button { Text = t, AutoSize = true, Tag = tag, Margin = new Padding(0, 3, 8, 3) }; b.Click += h; p.Controls.Add(b); return b;
    }
    static FlowLayoutPanel FlowRow() { return new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), Padding = new Padding(0) }; }

    Item Sel()
    {
        if (g.CurrentRow == null) return null;
        return g.CurrentRow.Tag as Item;
    }

    public void Reload()
    {
        try
        {
            g.Rows.Clear();
            foreach (DataRow r in db.Users().Rows)
            {
                var it = new Item
                {
                    Kind = "user", Name = (string)r["nm"], Login = r["lg"] as string, LType = r["lt"] as string,
                    Orphan = Convert.ToInt32(r["orphan"]) == 1, Prot = Convert.ToInt32(r["prot"]) == 1, SidHex = r["sidhex"] as string
                };
                string st = it.Prot ? "Защищён (admin/sa) — не трогаем" : it.Orphan ? "Сирота: логина на сервере нет" : "OK";
                if (!it.Prot && !it.Orphan && Convert.ToInt32(r["dis"]) == 1) st = "OK (логин отключён)";
                AddRow(it, st, it.Login, db.Kind == "mssql" ? TypeName(r["tp"] as string) : db.D.Title);
            }
            foreach (DataRow r in db.Logins().Rows)
            {
                var it = new Item { Kind = "login", Name = (string)r["nm"], Login = (string)r["nm"], LType = r["tp"] as string };
                AddRow(it, "Логин есть, в БД нет", it.Login, TypeName(it.LType));
            }
            var names = new List<string>(); foreach (DataGridViewRow gr in g.Rows) names.Add((string)gr.Cells[0].Value); pick.SetItems(names);
            Filter();
        }
        catch (Exception ex) { App.Log("Ошибка загрузки пользователей: " + ex.Message); UI.Warn(ex.Message); }
    }

    static string TypeName(string t) { return t == "S" ? "SQL" : t == "U" ? "Windows" : t == "G" ? "Группа Windows" : t; }

    void AddRow(Item it, string status, string login, string type)
    {
        int i = g.Rows.Add(it.Name, login ?? "—", type, status);
        var row = g.Rows[i]; row.Tag = it;
        Color c = it.Prot ? Color.Gray : it.Orphan ? Color.Firebrick : it.Kind == "login" ? Color.DarkOrange : Color.Empty;
        if (c != Color.Empty) row.DefaultCellStyle.ForeColor = c;
    }

    // --- создание ---
    string[] TplFiles()
    {
        try { string d = App.TemplatesDir; return Directory.Exists(d) ? Directory.GetFiles(d, "*.tpl") : new string[0]; }
        catch { return new string[0]; }
    }

    void Create(string preset)
    {
        using (var f = new CreateForm(preset, TemplateItems()))
        {
            if (db.Kind != "mssql")
            {
                f.PolicyOff.Visible = false;
                f.HintL.Text = db.Kind == "mysql" ? "Имя или имя@хост (по умолчанию хост «%» — откуда угодно). Если пользователь уже есть, пароль не меняется." : "Имя пользователя Firebird (хранится в верхнем регистре). Если пользователь уже есть, пароль не меняется.";
            }
            if (f.ShowDialog(this) != DialogResult.OK) return;
            string tpl = f.Tpl.SelectedIndex > 0 ? (string)f.Tpl.SelectedItem : null;
            try { DoCreate(f.TName.Text.Trim(), f.Pwd.T.Text, f.PolicyOff.Checked, tpl, false); }
            catch (Exception ex) { App.Log("ОШИБКА: " + ex.Message); UI.Warn(ex.Message); }
            Reload(); if (Changed != null) Changed();
        }
    }

    CreateResult Fin(bool quiet, bool ok, string msg, bool newLogin)
    {
        if (!quiet) { if (ok) UI.Info(msg); else UI.Warn(msg); }
        return new CreateResult { Ok = ok, Msg = msg, NewLogin = newLogin };
    }
    static CreateResult Cancel() { return new CreateResult { Ok = false, Msg = "Отменено" }; }
    static bool Confirm(bool quiet, string text) { return quiet || UI.Ask(text); }

    // quiet = без вопросов и окон (массовое создание): безопасные варианты выбираются автоматически
    CreateResult DoCreate(string name, string pwd, bool policyOff, string tpl, bool quiet)
    {
        if (db.Kind != "mssql") return CreateOther(name, pwd, tpl, quiet);
        if (name.Length == 0) return Fin(quiet, false, "Укажите имя.", false);
        string low = name.ToLower();
        if (low == "sa" || low == "dbo" || low == "guest" || low == "public") return Fin(quiet, false, "Это имя защищено, не трогаем.", false);
        bool win = name.IndexOf('\\') >= 0;
        bool loginEx = db.Count("SELECT COUNT(*) FROM sys.server_principals WHERE name=@n AND type IN ('S','U','G')", "@n", name) > 0;
        if (loginEx && db.IsSysadmin(name)) return Fin(quiet, false, "Логин входит в sysadmin — защищён, не трогаем.", false);
        bool userEx = db.Count("SELECT COUNT(*) FROM sys.database_principals WHERE name=@n AND type IN ('S','U','G')", "@n", name) > 0;
        if (userEx && db.IsDbOwner(name)) return Fin(quiet, false, "Пользователь — db_owner, защищён, не трогаем.", false);
        bool linked = userEx && loginEx && db.Count("SELECT COUNT(*) FROM sys.database_principals d JOIN sys.server_principals s ON s.sid=d.sid WHERE d.name=@n AND s.name=@n", "@n", name) > 0;
        bool newLogin = false; string what;

        if (!loginEx)
        {
            string sid = null;
            if (userEx)
            {
                if (!Confirm(quiet, "Пользователь «" + name + "» есть в БД, но логина на сервере нет (сирота).\nСоздать логин с тем же SID — права сохранятся?")) return Cancel();
                if (!win) sid = Convert.ToString(db.Query("SELECT CONVERT(varchar(200),sid,1) FROM sys.database_principals WHERE name=@n", "@n", name).Rows[0][0]);
            }
            if (!win && pwd.Length == 0) return Fin(quiet, false, "Нужен пароль.", false);
            CreateLogin(name, win, pwd, sid, policyOff); newLogin = true;
            if (!userEx) db.Exec("CREATE USER " + App.Q(name) + " FOR LOGIN " + App.Q(name), null);
            what = userEx ? "создан логин для сироты (права сохранены)" : "создан логин и пользователь БД";
        }
        else if (!userEx)
        {
            if (!Confirm(quiet, "Логин «" + name + "» уже есть на сервере, но в БД «" + db.Name + "» не добавлен.\nДобавить в БД (пароль не меняется)?")) return Cancel();
            db.Exec("CREATE USER " + App.Q(name) + " FOR LOGIN " + App.Q(name), null);
            what = "логин существовал, добавлен в БД (пароль не менялся)";
        }
        else if (!linked)
        {
            if (!Confirm(quiet, "Пользователь «" + name + "» в БД не привязан к одноимённому логину (SID не совпадает).\nПривязать (ALTER USER … WITH LOGIN)?")) return Cancel();
            db.Exec("ALTER USER " + App.Q(name) + " WITH LOGIN = " + App.Q(name), null);
            what = "пользователь привязан к логину";
        }
        else
        {
            if (tpl == null) return Fin(quiet, true, "Пользователь уже существует и привязан к логину.", false);
            what = "уже существовал, права добавлены";
        }
        if (tpl != null) ApplyTpl(name, tpl);
        App.Log("Готово: " + name + " — " + what);
        return Fin(quiet, true, quiet ? what : "Готово: " + name, newLogin);
    }

    // Firebird / MySQL: пользователь сервера + права на объекты текущей базы
    CreateResult CreateOther(string name, string pwd, string tpl, bool quiet)
    {
        name = db.D.Canon(name);
        if (name.Length == 0 || name == "@%") return Fin(quiet, false, "Укажите имя.", false);
        if (db.D.IsProtected(name)) return Fin(quiet, false, "Это имя защищено (администратор), не трогаем.", false);
        bool newUser = false; string what;
        if (!db.D.UserExists(name))
        {
            if (pwd.Length == 0) return Fin(quiet, false, "Нужен пароль.", false);
            db.D.CreateUser(name, pwd); newUser = true; what = "создан пользователь";
        }
        else
        {
            if (tpl == null) return Fin(quiet, true, "Пользователь уже существует.", false);
            what = "уже существовал, права добавлены";
        }
        if (tpl != null) ApplyTpl(name, tpl);
        App.Log("Готово: " + name + " — " + what);
        return Fin(quiet, true, quiet ? what : "Готово: " + name, newUser);
    }

    List<string> UserTplOther(string user)
    {
        var l = new List<string>();
        DataTable ot = db.D.Objects(new[] { "V", "U", "P", "FN" }), ut = db.Users(), pt = db.D.Perms(ot, ut);
        int pid = -1; foreach (DataRow r in ut.Rows) if (string.Equals((string)r["nm"], user, StringComparison.OrdinalIgnoreCase)) pid = Convert.ToInt32(r["pid"]);
        var names = new Dictionary<int, string[]>(); foreach (DataRow r in ot.Rows) names[Convert.ToInt32(r["oid"])] = new[] { Convert.ToString(r["sch"]), Convert.ToString(r["nm"]) };
        foreach (DataRow r in pt.Rows)
            if (Convert.ToInt32(r["gid"]) == pid) { string[] n = names[Convert.ToInt32(r["oid"])]; l.Add("P|" + n[0] + "|" + n[1] + "|" + Convert.ToString(r["pn"])); }
        return l;
    }

    void ApplyTplOther(string user, List<string> lines)
    {
        var okPerm = new List<string>(new[] { "SELECT", "EXECUTE" });
        DataTable ot = db.D.Objects(new[] { "V", "U", "P", "FN" });
        var byName = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow r in ot.Rows) byName[Convert.ToString(r["nm"])] = new[] { Convert.ToString(r["sch"]), Convert.ToString(r["tp"]) };
        var stm = new List<string>(); var inv = new List<string>();
        foreach (string ln in lines)
        {
            string[] p = ln.Split('|');
            if (p[0] != "P" || p.Length != 4 || !okPerm.Contains(p[3])) continue;
            string[] o;
            if (!byName.TryGetValue(p[2], out o)) { App.Log("Пропуск (нет объекта): " + p[2]); continue; }
            stm.Add(db.D.Grant(o[0], p[2], o[1], p[3], user)); inv.Add(db.D.Revoke(o[0], p[2], o[1], p[3], user));
        }
        db.Batch(stm, inv);
    }

    List<string> TemplateItems()
    {
        var items = new List<string>();
        foreach (DataRow r in db.Users().Rows)
            if (Convert.ToInt32(r["prot"]) == 0) items.Add("Пользователь: " + r["nm"]);
        foreach (string f in TplFiles()) items.Add("Шаблон: " + Path.GetFileNameWithoutExtension(f));
        return items;
    }

    void BulkCreate()
    {
        using (var f = new BulkForm(TemplateItems(), delegate(string n, string p, bool off, string tpl)
        {
            try { return DoCreate(n, p, off, tpl, true); }
            catch (Exception ex) { App.Log("ОШИБКА: " + n + ": " + ex.Message); return new CreateResult { Ok = false, Msg = ex.Message }; }
        }))
            f.ShowDialog(this);
        Reload(); if (Changed != null) Changed();
    }

    // добавить выбранному пользователю права и роли другого пользователя (ничего не отбирает)
    void CopyFrom()
    {
        var it = Sel();
        if (it == null || it.Kind != "user" || it.Prot) { UI.Warn("Выберите пользователя БД (не защищённого)."); return; }
        var src = new List<string>();
        foreach (DataRow r in db.Users().Rows)
            if (Convert.ToInt32(r["prot"]) == 0 && (string)r["nm"] != it.Name) src.Add((string)r["nm"]);
        if (src.Count == 0) { UI.Warn("Больше нет пользователей, у которых можно взять права."); return; }
        using (var f = new CopyForm("Права как у…", "Права и роли выбранного пользователя будут ДОБАВЛЕНЫ к правам «" + it.Name + "». Ничего не отбирается.", src, new List<string>(new[] { it.Name }), false))
        {
            if (f.ShowDialog(this) != DialogResult.OK || f.Src.SelectedItem == null) return;
            try { ApplyTpl(it.Name, "Пользователь: " + (string)f.Src.SelectedItem); UI.Info("Права добавлены."); }
            catch (Exception ex) { App.Log("ОШИБКА: " + ex.Message); UI.Warn(ex.Message); }
        }
    }

    void CreateLogin(string name, bool win, string pwd, string sidHex, bool policyOff)
    {
        string sql, shown;
        if (win) sql = shown = "CREATE LOGIN " + App.Q(name) + " FROM WINDOWS";
        else
        {
            string tail = (sidHex != null ? ", SID=" + sidHex : "") + (policyOff ? ", CHECK_POLICY=OFF" : "") + ", DEFAULT_DATABASE=" + App.Q(db.Name);
            sql = "CREATE LOGIN " + App.Q(name) + " WITH PASSWORD=" + App.L(pwd) + tail;
            shown = "CREATE LOGIN " + App.Q(name) + " WITH PASSWORD='***'" + tail;
        }
        db.Exec(sql, shown);
    }

    // --- шаблоны ---
    List<string> UserTpl(string user)
    {
        if (db.Kind != "mssql") return UserTplOther(user);
        var l = new List<string>();
        foreach (DataRow r in db.Query(@"SELECT s.name AS sch, o.name AS nm, dp.permission_name AS pn FROM sys.database_permissions dp
JOIN sys.objects o ON o.object_id=dp.major_id JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE dp.class=1 AND dp.minor_id=0 AND dp.state IN ('G','W') AND dp.grantee_principal_id=USER_ID(@u)", "@u", user).Rows)
            l.Add("P|" + r["sch"] + "|" + r["nm"] + "|" + r["pn"]);
        foreach (DataRow r in db.Query(@"SELECT r.name AS nm FROM sys.database_role_members m JOIN sys.database_principals r ON r.principal_id=m.role_principal_id
WHERE m.member_principal_id=USER_ID(@u) AND r.name NOT IN ('db_owner','db_accessadmin','db_securityadmin','public')", "@u", user).Rows)
            l.Add("R|" + r["nm"]);
        return l;
    }

    void ApplyTpl(string user, string tplItem)
    {
        string kind = tplItem.Substring(0, tplItem.IndexOf(':')), nm = tplItem.Substring(tplItem.IndexOf(": ") + 2);
        List<string> lines = kind == "Шаблон"
            ? new List<string>(File.ReadAllLines(Path.Combine(App.TemplatesDir, nm + ".tpl"), Encoding.UTF8))
            : UserTpl(nm);
        if (db.Kind != "mssql") { ApplyTplOther(user, lines); return; }
        var okPerm = new List<string>(new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "EXECUTE" });
        var badRole = new List<string>(new[] { "db_owner", "db_accessadmin", "db_securityadmin", "public" });
        db.InTx(delegate
        {
            foreach (string ln in lines)
            {
                string[] p = ln.Split('|');
                if (p[0] == "P" && p.Length == 4 && okPerm.Contains(p[3]))
                {
                    string full = App.Q(p[1]) + "." + App.Q(p[2]);
                    if (db.Count("SELECT CASE WHEN OBJECT_ID(@o) IS NULL THEN 0 ELSE 1 END", "@o", full) == 0) { App.Log("Пропуск (нет объекта): " + full); continue; }
                    db.Exec("GRANT " + p[3] + " ON OBJECT::" + full + " TO " + App.Q(user), null);
                }
                else if (p[0] == "R" && p.Length == 2 && !badRole.Contains(p[1]))
                {
                    if (db.Count("SELECT CASE WHEN DATABASE_PRINCIPAL_ID(@r) IS NULL THEN 0 ELSE 1 END", "@r", p[1]) == 0) { App.Log("Пропуск (нет роли): " + p[1]); continue; }
                    db.Exec("ALTER ROLE " + App.Q(p[1]) + " ADD MEMBER " + App.Q(user), null);
                }
            }
        });
    }

    void SaveTpl()
    {
        var it = Sel();
        if (it == null || it.Kind != "user") { UI.Warn("Выберите пользователя БД."); return; }
        string name = UI.Prompt("Сохранить шаблон", "Имя шаблона:", it.Name);
        if (string.IsNullOrEmpty(name)) return;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        try
        {
            string d = App.TemplatesDir; Directory.CreateDirectory(d);
            string f = Path.Combine(d, name + ".tpl");
            if (File.Exists(f) && !UI.Ask("Шаблон уже есть. Перезаписать?")) return;
            var l = UserTpl(it.Name);
            File.WriteAllLines(f, l.ToArray(), Encoding.UTF8);
            App.Log("Шаблон «" + name + "» сохранён (" + l.Count + " строк) из " + it.Name);
            UI.Info("Сохранено: " + l.Count + " прав/ролей.");
        }
        catch (Exception ex) { UI.Warn(ex.Message); }
    }

    // --- сирота / пароль ---
    void FixOrphan()
    {
        var it = Sel();
        if (it == null || it.Kind != "user" || !it.Orphan || it.Prot) { UI.Warn("Выберите строку со статусом «Сирота»."); return; }
        try { FixOrphanCore(it.Name, it.SidHex); }
        catch (Exception ex) { App.Log("ОШИБКА: " + ex.Message); UI.Warn(ex.Message); }
        Reload(); if (Changed != null) Changed();
    }

    // Сирота = пользователь в БД, у которого нет логина на сервере.
    // 1) есть логин с тем же именем -> привязать; 2) SQL-пользователь -> создать логин с тем же SID;
    // 3) Windows-пользователь -> создать логин Windows; если такой учётной записи уже нет — привязать к другому логину или удалить пользователя
    void FixOrphanCore(string name, string sidHex)
    {
        bool loginEx = db.Count("SELECT COUNT(*) FROM sys.server_principals WHERE name=@n AND type IN ('S','U','G') AND sid<>0x01", "@n", name) > 0 && !db.IsSysadmin(name);
        if (loginEx)
        {
            if (!UI.Ask("На сервере есть логин «" + name + "». Привязать к нему пользователя?")) return;
            db.Exec("ALTER USER " + App.Q(name) + " WITH LOGIN = " + App.Q(name), null);
            UI.Info("Готово."); return;
        }
        bool win = name.IndexOf('\\') >= 0;
        if (!win)
        {
            string pwd;
            using (var d = new PwdDialog("Создать логин для сироты", "Логина нет. Создадим логин «" + name + "» с тем же SID (права сохранятся).\nПароль:"))
            { if (d.ShowDialog(this) != DialogResult.OK) return; pwd = d.Pwd; }
            CreateLogin(name, false, pwd, sidHex, false);
            UI.Info("Готово: логин создан."); return;
        }
        try
        {
            CreateLogin(name, true, "", null, false);
            UI.Info("Готово: логин Windows создан, пользователь снова привязан.");
        }
        catch (Exception ex)
        {
            App.Log("Не удалось создать логин Windows «" + name + "»: " + ex.Message);
            int k = UI.Choose("Учётная запись Windows недоступна",
                "Не удалось создать логин для «" + name + "»:\n" + ex.Message + "\n\nОбычно это значит, что такой учётной записи Windows уже нет. Что сделать с пользователем в этой БД?",
                new[] { "Привязать к другому логину сервера (права сохранятся)", "Удалить пользователя из этой БД" });
            if (k == 0) RemapUser(name); else if (k == 1) DropUser(name);
        }
    }

    void RemapUser(string name)
    {
        var logins = new List<string>();
        foreach (DataRow r in db.Logins().Rows)
        {
            string tp = r["tp"] as string;
            if (tp == "U" || tp == "G") logins.Add((string)r["nm"]);   // пользователя Windows можно привязать только к логину Windows
        }
        if (logins.Count == 0) { UI.Warn("Нет свободных логинов Windows (пользователь Windows привязывается только к логину Windows).\nСоздайте такой логин или удалите пользователя из этой БД."); return; }
        string pick = UI.Pick("Привязать пользователя", "Логин Windows, к которому привязать «" + name + "» (права сохранятся):", logins);
        if (pick == null) return;
        if (!UI.Ask("Привязать «" + name + "» к логину «" + pick + "» и переименовать пользователя БД в «" + pick + "»?")) return;
        db.Exec("ALTER USER " + App.Q(name) + " WITH NAME = " + App.Q(pick) + ", LOGIN = " + App.Q(pick), null);
        UI.Info("Готово.");
    }

    void DropUser(string name)
    {
        if (!UI.Ask("Удалить пользователя «" + name + "» из базы «" + db.Name + "»?\nЕго права в этой БД пропадут, логины на сервере не затрагиваются.")) return;
        db.Exec("DROP USER " + App.Q(name), null);
        UI.Info("Пользователь удалён из БД.");
    }
    void ChangePwd()
    {
        var it = Sel();
        if (it == null || it.Prot || it.Login == null || it.LType != "S") { UI.Warn("Выберите обычного SQL-пользователя (не защищённого)."); return; }
        using (var d = new PwdDialog("Сменить пароль", "Новый пароль для «" + it.Login + "»:"))
        {
            if (d.ShowDialog(this) != DialogResult.OK || d.Pwd.Length == 0) return;
            try
            {
                if (db.Kind != "mssql") db.D.ChangePassword(it.Name, d.Pwd);
                else db.Exec("ALTER LOGIN " + App.Q(it.Login) + " WITH PASSWORD=" + App.L(d.Pwd), "ALTER LOGIN " + App.Q(it.Login) + " WITH PASSWORD='***'");
                UI.Info("Пароль изменён.");
            }
            catch (Exception ex) { App.Log("ОШИБКА: " + ex.Message); UI.Warn(ex.Message); }
        }
    }
}

// ===== матрица прав =====
class Matrix : Panel
{
    class Obj { public int Oid; public string Sch, Nm, Tp, Perm, Descr = ""; }
    class Usr { public int Pid; public string Name, Head; public bool Out; }

    Db db; string[] types, perms;
    DataGridView g = new DataGridView();
    FlatCombo cp = new FlatCombo(); Label lp = new Label(); int lastSel;
    HintBox tf = new HintBox(), tfU = new HintBox();
    CheckPicker objPick = new CheckPicker("Список объектов"), userPick = new CheckPicker("Список пользователей");
    FlatCombo cs = new FlatCombo(), cm = new FlatCombo();
    Label lInfo = new Label();
    List<Obj> objs = new List<Obj>(); List<Usr> usrs = new List<Usr>();
    Dictionary<string, char> perm = new Dictionary<string, char>();
    HashSet<string> pend = new HashSet<string>();
    bool loading;
    public bool Stale = true;
    public event Action Changed;

    static string Full(Obj o) { return o.Sch.Length > 0 ? o.Sch + "." + o.Nm : o.Nm; }
    static Color Pending { get { return Theme.Pending; } }
    static Color Denied { get { return Theme.Denied; } }
    const int FixedCols = 3;

    public Matrix(Db db, string[] types, string[] perms)
    {
        this.db = db; this.types = types; this.perms = perms;
        g.Dock = DockStyle.Fill; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.AllowUserToResizeRows = false;
        g.BackgroundColor = SystemColors.Window; g.RowHeadersWidth = 24; g.ColumnHeadersHeight = 80;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        g.EnableHeadersVisualStyles = true;
        g.CurrentCellDirtyStateChanged += delegate { if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        g.CellValueChanged += CellChanged;
        g.CellPainting += PaintBox;
        g.CellMouseClick += delegate(object o, DataGridViewCellMouseEventArgs a) { CellClick(a); };
        g.CellMouseEnter += delegate(object o, DataGridViewCellEventArgs a) { SetHover(a.RowIndex, a.ColumnIndex); };
        g.CellMouseLeave += delegate { SetHover(-1, -1); };
        g.KeyDown += delegate(object o, KeyEventArgs a)
        {
            if (a.KeyCode == Keys.Escape) { ClearMarks(); return; }
            if (a.KeyCode == Keys.Space && marks.Count > 0)
            {
                var ml = new List<DataGridViewCell>(marks); bool mv = !Val(ml[0]);
                ClearMarks(); SetCells(ml, mv); a.Handled = true; return;
            }
            if (a.KeyCode != Keys.Space) return;
            var sel = new List<DataGridViewCell>();
            foreach (DataGridViewCell c in g.SelectedCells) if (c.ColumnIndex >= FixedCols && c.RowIndex >= 0 && c.OwningRow.Visible) sel.Add(c);
            if (sel.Count == 0) return;
            bool target = !(sel[0].Value is bool && (bool)sel[0].Value);
            foreach (var c in sel) c.Value = target;
            a.Handled = true;
        };
        g.DataError += delegate { };
        g.ColumnHeaderMouseClick += HeaderClick;
        g.RowHeaderMouseClick += RowHeaderClick;

        // строка 1: фильтры, строка 2: действия (слева основные, справа вторичные)
        var filters = FlowRow(); filters.Dock = DockStyle.Top; filters.Padding = new Padding(0, 4, 0, 2);
        lp.Text = "Право:"; lp.AutoSize = true; lp.Margin = new Padding(0, 9, 6, 3);
        cp.Width = 120; cp.Margin = new Padding(0, 3, 12, 3);
        cp.SelectedIndexChanged += delegate
        {
            if (loading) return;
            if (!ConfirmDrop()) { loading = true; cp.SelectedIndex = lastSel; loading = false; return; }
            lastSel = cp.SelectedIndex; Fill();
        };
        filters.Controls.Add(lp); filters.Controls.Add(cp);
        SetPerms(perms);
        tf.Width = 200; tf.Margin = new Padding(0, 4, 8, 3); tf.Hint = "Поиск объектов…";
        tf.TextChanged += delegate { Filter(); }; filters.Controls.Add(tf);
        objPick.Margin = new Padding(0, 3, 8, 3); objPick.Changed += Filter; filters.Controls.Add(objPick);
        cs.Width = 130; cs.Margin = new Padding(0, 3, 8, 3); cs.SelectedIndexChanged += delegate { Filter(); }; filters.Controls.Add(cs);
        cm.Width = 160; cm.Margin = new Padding(0, 3, 24, 3);
        cm.Items.AddRange(new object[] { "Все", "Выдано кому-то", "Не выдано никому", "Изменённые" }); cm.SelectedIndex = 0;
        cm.SelectedIndexChanged += delegate { Filter(); }; filters.Controls.Add(cm);
        tfU.Width = 180; tfU.Margin = new Padding(0, 4, 8, 3); tfU.Hint = "Поиск пользователей…";
        tfU.TextChanged += delegate { FilterCols(); }; filters.Controls.Add(tfU);
        userPick.Margin = new Padding(0, 3, 0, 3); userPick.Changed += FilterCols; filters.Controls.Add(userPick);

        var actions = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 2, 0, 6) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = FlowRow(); left.Dock = DockStyle.Fill; var right = FlowRow(); right.WrapContents = false; right.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        AddBtn(left, "Применить", delegate { Apply(); }, "primary");
        AddBtn(left, "Откатить последнее", delegate { Undo(); }, "ghost");
        AddBtn(left, "Сбросить", delegate { if (pend.Count > 0 && ConfirmDrop()) Fill(); }, "ghost");
        AddBtn(left, "Копировать права…", delegate { CopyRights(); }, "ghost");
        AddBtn(right, "Экспорт CSV", delegate { Csv.Export(g, "rights"); }, "ghost");
        AddBtn(right, "Обновить", delegate { Reload(true); }, "ghost").Margin = new Padding(0, 3, 0, 3);
        actions.Controls.Add(left, 0, 0); actions.Controls.Add(right, 1, 0);        lInfo.Tag = "muted"; lInfo.AutoSize = true; lInfo.Margin = new Padding(0, 6, 3, 8); lInfo.Dock = DockStyle.Top;
        var lHint = new Label { Text = "Shift+клик — диапазон от последней ячейки  ·  Ctrl+клик — отметить ячейки, затем обычный клик (или пробел) ставит всем отмеченным  ·  чекбокс «Все» в шапке столбца и в строке — отметить весь столбец / всю строку  ·  Esc — снять отметки", AutoSize = true, Tag = "muted", Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
        Controls.Add(g); Controls.Add(lHint); Controls.Add(lInfo); Controls.Add(actions); Controls.Add(filters);
        SizeChanged += delegate { lInfo.MaximumSize = lHint.MaximumSize = new Size(Math.Max(300, ClientSize.Width - 8), 0); };
    }

    static Button AddBtn(Control p, string t, EventHandler h, string tag = null)
    {
        var b = new Button { Text = t, AutoSize = true, Tag = tag, Margin = new Padding(0, 3, 8, 3) }; b.Click += h; p.Controls.Add(b); return b;
    }
    static FlowLayoutPanel FlowRow() { return new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), Padding = new Padding(0) }; }

    bool ConfirmDrop() { return pend.Count == 0 || UI.Ask("Есть неприменённые изменения (" + pend.Count + "). Отбросить?"); }

    string PermFor(string tp)
    {
        if (perms == null) return (tp == "IF" || tp == "TF" || tp == "FT") ? "SELECT" : "EXECUTE";
        if (perms.Length == 1) return perms[0];
        return cp.SelectedItem != null ? (string)cp.SelectedItem : perms[0];
    }

    string Key(int pid, int oid, string p) { return pid + "|" + oid + "|" + p; }

    public void Reload(bool ask)
    {
        if (ask && !ConfirmDrop()) return;
        try
        {
            Cursor = Cursors.WaitCursor; loading = true;
            objs.Clear(); usrs.Clear(); perm.Clear();
            DataTable ot = db.D.Objects(types);
            foreach (DataRow r in ot.Rows)
                objs.Add(new Obj { Oid = Convert.ToInt32(r["oid"]), Sch = Convert.ToString(r["sch"]), Nm = Convert.ToString(r["nm"]), Tp = Convert.ToString(r["tp"]), Descr = ot.Columns.Contains("descr") ? Convert.ToString(r["descr"]).Trim() : "" });
            DataTable ut = db.Users();
            foreach (DataRow r in ut.Rows)
                if (Convert.ToInt32(r["prot"]) == 0)
                    usrs.Add(new Usr { Pid = Convert.ToInt32(r["pid"]), Name = (string)r["nm"], Head = (string)r["nm"] + (Convert.ToInt32(r["orphan"]) == 1 ? " ⚠" : "") });
            int outN = 0;
            foreach (DataRow r in db.Logins().Rows)
                usrs.Add(new Usr { Pid = --outN, Name = (string)r["nm"], Head = (string)r["nm"] + "\n(нет в БД)", Out = true });
            foreach (DataRow r in db.D.Perms(ot, ut).Rows)
                perm[Key(Convert.ToInt32(r["gid"]), Convert.ToInt32(r["oid"]), Convert.ToString(r["pn"]))] = Convert.ToString(r["st"])[0];

            var on = new List<string>(); foreach (Obj o in objs) on.Add(Full(o)); objPick.SetItems(on);
            var un = new List<string>(); foreach (Usr u in usrs) un.Add(u.Name); userPick.SetItems(un);
            g.Columns.Clear();
            AddCol("Объект", 300, true); AddCol("Все", 64, true); AddCol("Описание", 300, true);
            g.Columns[1].Tag = "nocsv"; g.Columns[1].ToolTipText = "Выдать или снять право всем пользователям в этой строке";
            g.Columns[2].DisplayIndex = 1;   // описание — сразу после имени объекта
            g.Columns[2].Visible = Cfg.ShowDescr && objs.Exists(delegate(Obj o) { return o.Descr.Length > 0; });
            foreach (Usr u in usrs)
            {
                var c = new DataGridViewCheckBoxColumn { HeaderText = u.Head, Width = 124, ReadOnly = true, Tag = u.Pid, SortMode = DataGridViewColumnSortMode.NotSortable };
                g.Columns.Add(c);
            }
            g.Columns[0].Frozen = true;
            string prevS = cs.SelectedIndex > 0 ? (string)cs.SelectedItem : null;
            cs.Items.Clear(); cs.Items.Add("Все схемы");
            foreach (Obj o in objs) if (o.Sch.Length > 0 && !cs.Items.Contains(o.Sch)) cs.Items.Add(o.Sch);
            cs.SelectedIndex = prevS != null && cs.Items.Contains(prevS) ? cs.Items.IndexOf(prevS) : 0;
            cs.Visible = cs.Items.Count > 1;
            Fill(); FilterCols();
            Stale = false;
        }
        catch (Exception ex) { App.Log("Ошибка загрузки: " + ex.Message); UI.Warn(ex.Message); }
        finally { loading = false; Cursor = Cursors.Default; }
    }

    // какие права показывает эта вкладка (из настроек); одно право — выбор скрыт
    public void SetPerms(string[] p)
    {
        perms = p;
        bool old = loading; loading = true;
        string prev = cp.SelectedItem as string;
        cp.Items.Clear();
        if (p != null)
        {
            cp.Items.AddRange(p);
            cp.SelectedIndex = prev != null && cp.Items.Contains(prev) ? cp.Items.IndexOf(prev) : 0;
        }
        lastSel = cp.SelectedIndex; loading = old;
        cp.Visible = lp.Visible = p != null && p.Length > 1;
        Stale = true;
    }

    void AddCol(string h, int w, bool ro)
    {
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, Width = w, ReadOnly = ro, SortMode = DataGridViewColumnSortMode.NotSortable });
    }

    void Fill()
    {
        bool old = loading; loading = true;
        try
        {
            g.CurrentCell = null;
            g.Rows.Clear(); pend.Clear();
            g.RowTemplate.Height = 34; marks.Clear(); anchor = null;
            foreach (Obj o in objs) o.Perm = PermFor(o.Tp);
            var vals = new object[FixedCols + usrs.Count];
            foreach (Obj o in objs)
            {
                vals[0] = Full(o); vals[1] = o.Tp; vals[2] = o.Descr;
                for (int i = 0; i < usrs.Count; i++)
                {
                    char st; perm.TryGetValue(Key(usrs[i].Pid, o.Oid, o.Perm), out st);
                    vals[FixedCols + i] = (st == 'G' || st == 'W');
                }
                int ri = g.Rows.Add(vals);
                g.Rows[ri].Tag = o;
                g.Rows[ri].Cells[0].ToolTipText = "Право: " + o.Perm + " · " + KindName(o.Tp);
                g.Rows[ri].Cells[2].ToolTipText = o.Descr;
                PaintRow(g.Rows[ri]);
            }
            Filter();
        }
        finally { loading = old; }
        UpdateInfo();
    }

    void Filter()
    {
        if (loading && g.Rows.Count == 0) return;
        var inc = new List<string>(); var exc = new List<string>();
        foreach (string w in tf.Text.ToLower().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        { if (w.Length > 1 && w[0] == '-') exc.Add(w.Substring(1)); else inc.Add(w); }
        string sch = cs.SelectedIndex > 0 ? (string)cs.SelectedItem : null;
        int mode = cm.SelectedIndex;
        g.CurrentCell = null;
        g.SuspendLayout();
        foreach (DataGridViewRow r in g.Rows)
        {
            var o = (Obj)r.Tag;
            string n = (Full(o) + " " + o.Descr).ToLower();
            bool ok = sch == null || o.Sch == sch;
            if (ok && objPick.Checked.Count > 0 && !objPick.Checked.Contains(Full(o))) ok = false;
            foreach (string w in inc) if (ok && !n.Contains(w)) ok = false;
            foreach (string w in exc) if (ok && n.Contains(w)) ok = false;
            if (ok && mode > 0)
            {
                bool any = false, chg = false;
                for (int c = FixedCols; c < g.Columns.Count; c++)
                {
                    if (r.Cells[c].Value is bool && (bool)r.Cells[c].Value) any = true;
                    if (pend.Contains(Key((int)g.Columns[c].Tag, o.Oid, o.Perm))) chg = true;
                }
                ok = mode == 1 ? any : mode == 2 ? !any : chg;
            }
            r.Visible = ok;
        }
        g.ResumeLayout();
        UpdateInfo();
    }

    void FilterCols()
    {
        var inc = tfU.Text.ToLower().Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (int c = FixedCols; c < g.Columns.Count; c++)
        {
            string n = g.Columns[c].HeaderText.ToLower(); bool ok = inc.Length == 0;
            foreach (string w in inc) if (n.Contains(w)) ok = true;
            if (ok && userPick.Checked.Count > 0 && !userPick.Checked.Contains(usrs[c - FixedCols].Name)) ok = false;
            g.Columns[c].Visible = ok;
        }
    }

    void UpdateInfo()
    {
        int vis = 0; foreach (DataGridViewRow r in g.Rows) if (r.Visible) vis++;
        lInfo.Text = "Показано " + vis + " из " + objs.Count + ", пользователей и логинов: " + usrs.Count + (pend.Count > 0 ? ", изменений: " + pend.Count : "") +
            "   Ячейки: зелёная — доступ есть, жёлтая — ждёт применения, красная — DENY. Объект: красный — ни у кого нет доступа, зелёный — у всех.";
    }

    bool Orig(Obj o, int pid)
    {
        char st; perm.TryGetValue(Key(pid, o.Oid, o.Perm), out st);
        return st == 'G' || st == 'W';
    }

    static string KindName(string tp)
    {
        switch (tp)
        {
            case "V": return "представление";
            case "U": return "таблица";
            case "P": case "PC": return "процедура";
            case "FN": case "FS": return "скалярная функция";
            case "IF": case "TF": case "FT": return "табличная функция";
            case "AF": return "агрегатная функция";
        }
        return tp;
    }

    // ячейка: жёлтая — ждёт применения, красная — DENY, зелёная — доступ есть, без цвета — нет доступа
    void PaintCell(DataGridViewRow r, int c)
    {
        var o = (Obj)r.Tag;
        string k = Key((int)g.Columns[c].Tag, o.Oid, o.Perm);
        char st; perm.TryGetValue(k, out st);
        bool v = r.Cells[c].Value is bool && (bool)r.Cells[c].Value;
        r.Cells[c].Style.BackColor = pend.Contains(k) ? Pending : v ? Theme.Allow : st == (char)68 ? Denied : Color.Empty;
    }

    // имя объекта: красное — доступа нет ни у кого, зелёное — доступ у всех пользователей БД
    void PaintObj(DataGridViewRow r)
    {
        int all = 0, n = 0;
        for (int c = FixedCols; c < g.Columns.Count; c++)
        {
            if (usrs[c - FixedCols].Out) continue;
            all++;
            if (r.Cells[c].Value is bool && (bool)r.Cells[c].Value) n++;
        }
        r.Cells[0].Style.BackColor = all == 0 ? Color.Empty : n == 0 ? Theme.NoneRow : n == all ? Theme.AllRow : Color.Empty;
    }

    void PaintRow(DataGridViewRow r)
    {
        for (int c = FixedCols; c < g.Columns.Count; c++) PaintCell(r, c);
        PaintObj(r);
    }

    public void Recolor()
    {
        foreach (DataGridViewRow r in g.Rows) PaintRow(r);
    }

    // --- массовая расстановка ---
    // клик — переключить; Shift+клик — диапазон от последней ячейки (с её состоянием);
    // Ctrl+клик — отметить ячейку (рамка), затем обычный клик или пробел ставит всем отмеченным
    HashSet<DataGridViewCell> marks = new HashSet<DataGridViewCell>();
    DataGridViewCell anchor; bool anchorVal;
    static bool Val(DataGridViewCell c) { return c.Value is bool && (bool)c.Value; }
    static void SetCells(IEnumerable<DataGridViewCell> cells, bool v)
    {
        foreach (var c in new List<DataGridViewCell>(cells)) if (Val(c) != v) c.Value = v;
    }
    void ClearMarks()
    {
        var old = new List<DataGridViewCell>(marks); marks.Clear();
        foreach (var x in old) if (x.DataGridView == g) g.InvalidateCell(x);
    }
    void CellClick(DataGridViewCellMouseEventArgs a)
    {
        if (a.Button == MouseButtons.Left && a.RowIndex >= 0 && a.ColumnIndex == 1) { ToggleRow(a.RowIndex); return; }
        if (a.Button != MouseButtons.Left || a.RowIndex < 0 || a.ColumnIndex < FixedCols) return;
        var c = g.Rows[a.RowIndex].Cells[a.ColumnIndex];
        bool ctrl = (Control.ModifierKeys & Keys.Control) != 0, shift = (Control.ModifierKeys & Keys.Shift) != 0;
        if (ctrl) { if (!marks.Remove(c)) marks.Add(c); g.InvalidateCell(c); return; }
        if (shift && anchor != null && anchor.DataGridView == g)
        {
            int r1 = Math.Min(anchor.RowIndex, a.RowIndex), r2 = Math.Max(anchor.RowIndex, a.RowIndex);
            int c1 = Math.Min(anchor.ColumnIndex, a.ColumnIndex), c2 = Math.Max(anchor.ColumnIndex, a.ColumnIndex);
            var list = new List<DataGridViewCell>();
            for (int r = r1; r <= r2; r++)
            {
                var row = g.Rows[r]; if (!row.Visible) continue;
                for (int cc = c1; cc <= c2; cc++) if (cc >= FixedCols && g.Columns[cc].Visible) list.Add(row.Cells[cc]);
            }
            SetCells(list, anchorVal); return;
        }
        if (marks.Count > 0)
        {
            bool v = !Val(c); var l = new List<DataGridViewCell>(marks); l.Add(c);
            ClearMarks(); SetCells(l, v); anchor = c; anchorVal = v; return;
        }
        bool nv = !Val(c); c.Value = nv; anchor = c; anchorVal = nv;
    }

    int hr = -1, hc = -1;
    int hh = -1;   // столбец, над шапкой которого курсор
    void SetHover(int r, int c)
    {
        int or = hr, oc = hc, oh = hh; hr = r; hc = c;
        hh = (r == -1 && c >= FixedCols) ? c : -1;
        if (or >= 0 && oc >= 0 && or < g.RowCount && oc < g.ColumnCount) g.InvalidateCell(oc, or);
        if (r >= 0 && (c >= FixedCols || c == 1)) g.InvalidateCell(c, r);
        if (oh >= 0 && oh < g.ColumnCount && oh != hh) g.InvalidateCell(oh, -1);
        if (hh >= 0) g.InvalidateCell(hh, -1);
        bool act = (r == -1 && c >= FixedCols) || (r >= 0 && (c >= FixedCols || c == 1));
        g.Cursor = act ? Cursors.Hand : Cursors.Default;
    }

    // состояние: 0 — нигде, 1 — частично, 2 — везде (по видимым строкам / столбцам)
    int RowState(DataGridViewRow r)
    {
        int n = 0, all = 0;
        for (int c = FixedCols; c < g.Columns.Count; c++)
            if (g.Columns[c].Visible) { all++; if (Val(r.Cells[c])) n++; }
        return all == 0 || n == 0 ? 0 : n == all ? 2 : 1;
    }
    int ColState(int c)
    {
        int n = 0, all = 0;
        foreach (DataGridViewRow r in g.Rows) if (r.Visible) { all++; if (Val(r.Cells[c])) n++; }
        return all == 0 || n == 0 ? 0 : n == all ? 2 : 1;
    }
    void ToggleRow(int ri)
    {
        var r = g.Rows[ri]; var cells = new List<DataGridViewCell>(); bool any = false;
        for (int c = FixedCols; c < g.Columns.Count; c++)
            if (g.Columns[c].Visible) { cells.Add(r.Cells[c]); if (!Val(r.Cells[c])) any = true; }
        SetCells(cells, any);
    }

    // шапка столбца: имя пользователя + явный чекбокс «Все» (клик по шапке — весь столбец)
    void PaintHeader(DataGridViewCellPaintingEventArgs e)
    {
        e.Paint(e.ClipBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
        var b = e.CellBounds;
        bool hov = e.ColumnIndex == hh;
        if (hov) using (var br = new SolidBrush(Color.FromArgb(40, Theme.Accent))) e.Graphics.FillRectangle(br, b.X + 1, b.Y + 1, b.Width - 2, b.Height - 2);
        Font f = g.ColumnHeadersDefaultCellStyle.Font ?? g.Font;
        TextRenderer.DrawText(e.Graphics, Convert.ToString(e.Value), f, new Rectangle(b.X + 6, b.Y + 4, b.Width - 10, b.Height - 36), Theme.Fg,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.Top);
        int st = ColState(e.ColumnIndex);
        Box.Draw(e.Graphics, new Rectangle(b.X + 8, b.Bottom - 28, 20, 20), st == 2, hov, st == 1);
        TextRenderer.DrawText(e.Graphics, "Все", f, new Rectangle(b.X + 32, b.Bottom - 30, b.Width - 36, 24), Theme.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        e.Handled = true;
    }

    void PaintBox(object s, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex == -1 && e.ColumnIndex >= FixedCols) { PaintHeader(e); return; }
        if (e.RowIndex >= 0 && e.ColumnIndex == 1)
        {
            e.Paint(e.ClipBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
            int s1 = RowState(g.Rows[e.RowIndex]), z = Math.Min(22, e.CellBounds.Height - 8);
            Box.Draw(e.Graphics, new Rectangle(e.CellBounds.X + (e.CellBounds.Width - z) / 2, e.CellBounds.Y + (e.CellBounds.Height - z) / 2, z, z), s1 == 2, e.RowIndex == hr && e.ColumnIndex == hc, s1 == 1);
            e.Handled = true; return;
        }
        if (e.RowIndex < 0 || e.ColumnIndex < FixedCols) return;
        e.Paint(e.ClipBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
        int sz = Math.Min(22, e.CellBounds.Height - 8);
        var r = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - sz) / 2, e.CellBounds.Y + (e.CellBounds.Height - sz) / 2, sz, sz);
        Box.Draw(e.Graphics, r, e.Value is bool && (bool)e.Value, e.RowIndex == hr && e.ColumnIndex == hc);
        if (marks.Count > 0 && marks.Contains(g.Rows[e.RowIndex].Cells[e.ColumnIndex]))
            using (var p = new Pen(Theme.Accent, 2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                e.Graphics.DrawRectangle(p, e.CellBounds.X + 2, e.CellBounds.Y + 2, e.CellBounds.Width - 5, e.CellBounds.Height - 5);
        e.Handled = true;
    }

    void CellChanged(object s, DataGridViewCellEventArgs e)
    {
        if (loading || e.RowIndex < 0 || e.ColumnIndex < FixedCols) return;
        var row = g.Rows[e.RowIndex]; var o = (Obj)row.Tag; var cell = row.Cells[e.ColumnIndex];
        int pid = (int)g.Columns[e.ColumnIndex].Tag;
        bool v = cell.Value is bool && (bool)cell.Value;
        string k = Key(pid, o.Oid, o.Perm);
        char st; perm.TryGetValue(k, out st);
        if (v != Orig(o, pid)) pend.Add(k); else pend.Remove(k);
        PaintCell(row, e.ColumnIndex); PaintObj(row);
        g.InvalidateCell(1, e.RowIndex); g.InvalidateCell(e.ColumnIndex, -1);
        UpdateInfo();
    }

    void HeaderClick(object s, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < FixedCols) return;
        bool any = false;
        foreach (DataGridViewRow r in g.Rows) if (r.Visible && !(bool)r.Cells[e.ColumnIndex].Value) { any = true; break; }
        foreach (DataGridViewRow r in g.Rows) if (r.Visible) r.Cells[e.ColumnIndex].Value = any;
    }

    void RowHeaderClick(object s, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0) return;
        ToggleRow(e.RowIndex);
    }

    void Apply()
    {
        if (g.IsCurrentCellInEditMode) g.EndEdit();
        var stm = new List<string>(); var inv = new List<string>();
        foreach (DataGridViewRow r in g.Rows)
        {
            var o = (Obj)r.Tag;
            for (int c = FixedCols; c < g.Columns.Count; c++)
            {
                int pid = (int)g.Columns[c].Tag;
                string k = Key(pid, o.Oid, o.Perm);
                if (!pend.Contains(k)) continue;
                bool v = (bool)r.Cells[c].Value;
                string user = usrs[c - FixedCols].Name;
                char st; perm.TryGetValue(k, out st);
                if (v)
                {
                    stm.Add(db.D.Grant(o.Sch, o.Nm, o.Tp, o.Perm, user));
                    string dn = st == (char)68 ? db.D.Deny(o.Sch, o.Nm, o.Tp, o.Perm, user) : null;
                    inv.Add(dn ?? db.D.Revoke(o.Sch, o.Nm, o.Tp, o.Perm, user));
                }
                else
                {
                    stm.Add(db.D.Revoke(o.Sch, o.Nm, o.Tp, o.Perm, user));
                    inv.Add(db.D.Grant(o.Sch, o.Nm, o.Tp, o.Perm, user));
                }
            }
        }
        if (stm.Count == 0) { UI.Info("Нет изменений."); return; }
        var addU = new List<string>();
        for (int c = FixedCols; c < g.Columns.Count; c++)
        {
            Usr u = usrs[c - FixedCols];
            if (!u.Out) continue;
            foreach (DataGridViewRow r in g.Rows)
                if (pend.Contains(Key(u.Pid, ((Obj)r.Tag).Oid, ((Obj)r.Tag).Perm))) { addU.Add(u.Name); break; }
        }
        if (addU.Count > 0 && !UI.Ask("Эти логины ещё не добавлены в базу «" + db.Name + "»:\n  " + string.Join(", ", addU.ToArray()) + "\n\nДобавить их как пользователей БД и выдать права?")) return;
        string how = db.D.Transactional ? "Всё выполняется одной транзакцией — при ошибке откат." : "При ошибке уже выполненные команды будут отменены обратными командами.";
        if (Cfg.ConfirmApply && !UI.Ask("Применить изменений: " + stm.Count + "?\n" + how)) return;
        var allStm = new List<string>(); var allInv = new List<string>();
        foreach (string n in addU) { allStm.Add("CREATE USER " + App.Q(n) + " FOR LOGIN " + App.Q(n)); allInv.Add("DROP USER " + App.Q(n)); }
        allStm.AddRange(stm); allInv.AddRange(inv);
        try
        {
            db.Batch(allStm, allInv);
            App.Log("Применено изменений: " + stm.Count);
        }
        catch (Exception ex) { App.Log("ОШИБКА: " + ex.Message); UI.Warn("Ошибка, изменения отменены:\n" + ex.Message); Reload(false); return; }
        var undo = new List<string>(allInv); undo.Reverse();
        History.Stack.Add(new UndoItem { Db = db.Name, Title = DateTime.Now.ToString("HH:mm") + " — изменений: " + stm.Count + (addU.Count > 0 ? ", новых пользователей БД: " + addU.Count : ""), Sql = undo });
        Reload(false);
        if (Changed != null) Changed();
    }

    // откат последнего применения для этой базы (обратные команды)
    void Undo()
    {
        UndoItem it = null;
        for (int i = History.Stack.Count - 1; i >= 0; i--) if (History.Stack[i].Db == db.Name) { it = History.Stack[i]; break; }
        if (it == null) { UI.Info("Откатывать нечего: в этой сессии для базы «" + db.Name + "» изменений не применялось."); return; }
        if (pend.Count > 0 && !ConfirmDrop()) return;
        if (Cfg.ConfirmUndo && !UI.Ask("Откатить применение: " + it.Title + "?\nБудет выполнено команд: " + it.Sql.Count + (db.D.Transactional ? " (одной транзакцией)." : "."))) return;
        try
        {
            db.Batch(it.Sql, null);
            History.Stack.Remove(it);
            App.Log("Откат выполнен: " + it.Title);
        }
        catch (Exception ex) { App.Log("ОШИБКА отката: " + ex.Message); UI.Warn("Не удалось откатить:\n" + ex.Message); return; }
        Reload(false);
        if (Changed != null) Changed();
    }

    void CopyRights()
    {
        var src = new List<string>(); var dst = new List<string>();
        foreach (Usr u in usrs) { if (!u.Out) src.Add(u.Name); dst.Add(u.Name); }
        if (src.Count == 0) { UI.Warn("Нет пользователей БД, у которых можно взять права."); return; }
        using (var f = new CopyForm("Копировать права", "Права копируются по строкам, которые видны сейчас (учитывается фильтр). Изменения отметятся жёлтым — затем нажмите «Применить».", src, dst, true))
        {
            if (f.ShowDialog(this) != DialogResult.OK || f.Src.SelectedItem == null || f.Dst.SelectedItem == null) return;
            int n = DoCopy((string)f.Src.SelectedItem, (string)f.Dst.SelectedItem, f.Mode.SelectedIndex == 0);
            if (n >= 0) UI.Info(n == 0 ? "Менять нечего: права уже совпадают." : "Отмечено изменений: " + n + ". Проверьте и нажмите «Применить».");
        }
    }

    // переносит отметки из столбца src в столбец dst (replace: точно как у src; иначе только добавляет). Возвращает число изменённых ячеек.
    public int DoCopy(string src, string dst, bool replace)
    {
        if (src == dst) { UI.Warn("Источник и получатель совпадают."); return -1; }
        int si = -1, di = -1;
        for (int c = FixedCols; c < g.Columns.Count; c++)
        {
            string n = usrs[c - FixedCols].Name;
            if (n == src) si = c;
            if (n == dst) di = c;
        }
        if (si < 0 || di < 0) return -1;
        int cnt = 0;
        foreach (DataGridViewRow r in g.Rows)
        {
            if (!r.Visible) continue;
            bool sv = (bool)r.Cells[si].Value, dv = (bool)r.Cells[di].Value;
            bool nv = replace ? sv : (sv || dv);
            if (nv != dv) { r.Cells[di].Value = nv; cnt++; }
        }
        return cnt;
    }
}

// ===== главное окно =====
class MainForm : Form
{
    public bool Reconnect;
    Db db;
    Panel body = new Panel();
    FlowLayoutPanel strip = new FlowLayoutPanel();
    List<Control> pages = new List<Control>();
    List<Button> tabBtns = new List<Button>();
    TextBox log = new TextBox();
    UsersTab users;
    List<Matrix> mats = new List<Matrix>();
    Button bTheme = new Button();
    DescriptionsTab desc;

    public MainForm(Db db)
    {
        this.db = db;
        Text = "LoginChanger — " + db.Server + " / " + db.Name + " (" + db.Who + ")";
        AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 480); Size = new Size(1150, 760);

        users = new UsersTab(db);
        AddPage("Пользователи", users);
        AddMat(Cap("Представления", Cfg.ViewPerms), new Matrix(db, new[] { "V" }, Cfg.ViewPerms.ToArray()));
        AddMat("Функции и процедуры (EXECUTE)", new Matrix(db, new[] { "P", "PC", "FN", "IF", "TF", "FS", "FT", "AF" }, null));
        AddMat(Cap("Таблицы", Cfg.TablePerms), new Matrix(db, new[] { "U" }, Cfg.TablePerms.ToArray()));
        desc = new DescriptionsTab(db); AddPage("Описания", desc);
        desc.Changed += delegate { foreach (var m in mats) m.Stale = true; };

        body.Dock = DockStyle.Fill; body.Padding = new Padding(12, 4, 12, 8);
        strip.Dock = DockStyle.Top; strip.AutoSize = true; strip.Padding = new Padding(12, 8, 12, 4);
        log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.Dock = DockStyle.Fill;
        log.Font = new Font("Consolas", 9.5f);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, Panel2MinSize = 50, SplitterWidth = 6 };
        split.Panel1.Controls.Add(body); split.Panel1.Controls.Add(strip);
        split.Panel2.Padding = new Padding(12, 6, 12, 10); split.Panel2.Controls.Add(log);

        var hdr = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 10, 12, 4) };
        hdr.Controls.Add(new Label { Text = "LoginChanger", AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(3, 2, 14, 0) });
        hdr.Controls.Add(new Label { Text = db.D.Title + "  ›  " + db.Server + "  ›  " + db.Name + "  ›  " + db.Who, AutoSize = true, Tag = "muted", Margin = new Padding(3, 9, 14, 0) });
        var bSw = new Button { Text = "Сменить сервер / БД", AutoSize = true };
        bSw.Click += delegate { Reconnect = true; Close(); };
        bTheme.Text = Theme.Label; bTheme.AutoSize = true;
        bTheme.Click += delegate
        {
            Theme.Toggle(); Theme.Apply(this); bTheme.Text = Theme.Label;
            foreach (var m in mats) m.Recolor();
        };
        var bSet = new Button { Text = "Настройки", AutoSize = true };
        bSet.Click += delegate
        {
            using (var sf = new SettingsForm()) if (sf.ShowDialog(this) == DialogResult.OK) ApplySettings();
        };
        hdr.Controls.Add(bSw); hdr.Controls.Add(bSet); hdr.Controls.Add(bTheme);
        Controls.Add(split); Controls.Add(hdr);

        App.OnLog += AppendLog;
        FormClosed += delegate { App.OnLog -= AppendLog; db.C.Dispose(); };
        Load += delegate
        {
            try { split.SplitterDistance = Math.Max(100, split.Height - 150); } catch { }
            users.Reload();
            Select(0);
        };
        users.Changed += delegate { foreach (var m in mats) m.Stale = true; };
        foreach (var mm in mats)
        {
            var cur = mm;
            cur.Changed += delegate { users.Reload(); foreach (var x in mats) if (x != cur) x.Stale = true; desc.Stale = true; };
        }
        Theme.Apply(this);
    }

    void AppendLog(string s) { log.AppendText(s + "\r\n"); }

    static string Cap(string title, List<string> perms) { return perms.Count == 1 ? title + " (" + perms[0] + ")" : title; }
    int cur;

    // применить новые настройки к открытому окну (тема, права на вкладках, колонка «Описание»)
    void ApplySettings()
    {
        Theme.Init(Cfg.IsDark()); Theme.Apply(this); bTheme.Text = Theme.Label;
        foreach (var m in mats) m.Recolor();
        mats[0].SetPerms(Cfg.ViewPerms.ToArray()); mats[2].SetPerms(Cfg.TablePerms.ToArray());
        foreach (var m in mats) m.Stale = true;
        desc.Stale = true;
        tabBtns[1].Text = Cap("Представления", Cfg.ViewPerms); tabBtns[3].Text = Cap("Таблицы", Cfg.TablePerms);
        Select(cur);
    }

    void AddPage(string t, Control c)
    {
        c.Dock = DockStyle.Fill; c.Visible = false; body.Controls.Add(c);
        pages.Add(c);
        int idx = pages.Count - 1;
        var b = new Button { Text = t, AutoSize = true, Tag = "tab", Margin = new Padding(0, 0, 6, 6), Padding = new Padding(8, 2, 8, 2) };
        b.Click += delegate { Select(idx); };
        strip.Controls.Add(b); tabBtns.Add(b);
    }
    void AddMat(string t, Matrix m) { AddPage(t, m); mats.Add(m); }

    void Select(int i)
    {
        cur = i;
        for (int j = 0; j < pages.Count; j++)
        {
            pages[j].Visible = j == i;
            tabBtns[j].Tag = j == i ? "tabsel" : "tab";
            Theme.StyleButton(tabBtns[j]);
        }
        var m = pages[i] as Matrix;
        if (m != null && m.Stale) m.Reload(true);
        var dt = pages[i] as DescriptionsTab; if (dt != null && dt.Stale) dt.Reload(true);
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Cfg.Load(); App.PurgeLogs(); Theme.Init(Cfg.IsDark());
        App.Log("=== запуск ===");
        while (true)
        {
            Db db;
            using (var cf = new ConnForm())
            {
                if (cf.ShowDialog() != DialogResult.OK) return;
                db = cf.Result;
            }
            bool again;
            using (var mf = new MainForm(db)) { mf.ShowDialog(); again = mf.Reconnect; }
            if (!again) return;
        }
    }
}
