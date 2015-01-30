// minimalistic telnet implementation
// conceived by Tom Janssens on 2007/06/06 for codeproject
//
// http://www.corebvba.be

// Modifications
//
// Date Person Description
// ========== ========= ==============================================================================
// 2013-06-06 jsagara   Implements IDisposable. Miscellaneous refactoring.
// 2014-05-15 mewiii    Implements new methods for working with HP switches;
//                      Implements setter/getter methods to change the default timeout value;
//                      Implements new methods for reading until some text you're looking for is found
//                      Implements Close() method to disconnect from remote host
// 2015-02-05 0xbadf00d Implements prompt variable and uses it with ReadUntilString to capture returned
//                      data using the Command() method.
//                      Implements Enable() & isEnabled() to enable Cisco devices and check status.
//                      Implements char[] _toTrim as null characters seen on some Cisco Nexus devices

using System;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MinimalisticTelnet
{
    public class TelnetConnection : IDisposable
    {
        private TcpClient tcpSocket;
        private int _TimeoutMs = 100;
        private int _ReadUntilLimit = 3;
        private string _prompt = "";
        private char[] _toTrim = { '\n', '\r', '\0' };

        public bool IsConnected
        {
            get { return tcpSocket.Connected; }
        }

        public int TimeoutMs
        {
            set { this._TimeoutMs = value; }
            get { return _TimeoutMs; }
        }

        public int ReadUntilLimit
        {
            set { this._ReadUntilLimit = value; }           
            get { return _ReadUntilLimit; }
        }

        public TelnetConnection(string hostname, int port)
        {
            tcpSocket = new TcpClient(hostname, port);
        }

        ~TelnetConnection()
        {
            Dispose(false);
        }

        // prompt set by Login when last line is trimmed using char[] _toTrim
        public string Login(string username, string password, int loginTimeoutMs)
        {
            int oldTimeoutMs = TimeoutMs;
            TimeoutMs = loginTimeoutMs;

            string s = Read();
            if (!s.TrimEnd().EndsWith(":"))
            {
                throw new Exception("Failed to connect : no login prompt");
            }

            WriteLine(username);

            s += Read();
            if (!s.TrimEnd().EndsWith(":"))
            {
                throw new Exception("Failed to connect : no password prompt");
            }

            WriteLine(password);

            s += Read();

            TimeoutMs = oldTimeoutMs;

            Match match = Regex.Match(s, "^.*$", RegexOptions.Multiline | RegexOptions.RightToLeft);
            if (match.Success)
            {
                _prompt = match.Value.Trim(_toTrim);
            }

            return s;
        }

        // added this method useful with Cisco routers that ask for a password only
        // based on Login() above
        public string PasswordOnlyLogin(string Password, int LoginTimeoutMs)
        {
            int oldTimeoutMs = TimeoutMs;
            TimeoutMs = LoginTimeoutMs;
            string s = Read();

            if (!s.TrimEnd().EndsWith(":"))
                throw new Exception("Failed to connect : no password prompt");
            WriteLine(Password);

            s += Read();
            TimeoutMs = oldTimeoutMs;
            return s;
        }

        // added this generic method useful with HP Switches that require that a space
        // be sent prior to entering the password
        // the user name is optional when it is left empty
        // HP switches output lots of escape sequences and it is unlikely a prompt cleanly ends with #, $, >, etc.
        public string HPGenericLogin(string Username, string Password, bool sendSpace, int LoginTimeoutMs)
        {
            int oldTimeoutMs = TimeoutMs;
            TimeoutMs = LoginTimeoutMs;
            string s = "";

            if (sendSpace)
            {
                s = ReadUntilString("Press any key to continue");
                WriteLine(" ");
            }

            if (Username.Length != 0)
            {
                s += ReadUntilString("Username:");
                WriteLine(Username);
            }

            s += ReadUntilString("Password:");
            WriteLine(Password);

            TimeoutMs = oldTimeoutMs;
            return s;
        }

        public string HPLogout()
        {
            WriteLine("logout");
            string s = ReadUntilString("log out [y/n]?");
            WriteLine("y");

            this.Close();

            return s;
        }

        public string HPLogout(int LogoutTimeoutMs)
        {
            int oldTimeoutMs = TimeoutMs;
            TimeoutMs = LogoutTimeoutMs;

            string s = HPLogout();

            TimeoutMs = oldTimeoutMs;

            return s;
        }

        public void WriteLine(string cmd)
        {
            Write(cmd + "\n");
        }

        public void Write(string cmd)
        {
            if (!tcpSocket.Connected)
            {
                return;
            }

            byte[] buf = ASCIIEncoding.ASCII.GetBytes(cmd.Replace("\0xFF", "\0xFF\0xFF"));
            tcpSocket.GetStream().Write(buf, 0, buf.Length);
        }

        public string Read()
        {
            if (!tcpSocket.Connected)
            {
                return null;
            }

            var sb = new StringBuilder();
            do
            {
                ParseTelnet(sb);
                Thread.Sleep(TimeoutMs);

            } while (tcpSocket.Available > 0);

            return sb.ToString();
        }

        // based on Read()
        // read until find text is found in the input stream
        public string ReadUntilString(string findText)
        {
            int counter = ReadUntilLimit;
            string s = Read();
            while (!s.Contains(findText))
            {
                string s2 = Read();
                if (s2.Length == 0)
                {
                    counter -= 1;
                    if (counter < 1)
                    {
                        throw new Exception("Failed to receive find text : " + findText);
                    }
                }
                else
                {
                    counter = ReadUntilLimit;
                }
                s += s2;
            }
            return s;
        }

        //based on ReadUntilString(string findnext)
        //using the prompt variable send the command and reads until the prompt is found.
        public string Command(string command)
        {
            WriteLine(command);
            int counter = ReadUntilLimit;
            string s = Read();
            while (!s.Contains(_prompt))
            {
                string s2 = Read();
                if (s2.Length == 0)
                {
                    counter -= 1;
                    if (counter < 1)
                    {
                        throw new Exception("Failed to recieve prompt after command : " + command);
                    }
                }
                else
                {
                    counter = ReadUntilLimit;
                }
                s += s2;
            }
            return s;
            
        }

        // Enable method based on Login, prompt set after enable attempted
        public void Enable(string enablePassword, int EnableTimeoutMs)
        {
            int oldTimeoutMs = TimeoutMs;
            TimeoutMs = EnableTimeoutMs;

            WriteLine("enable");
            string s = Read();

            if (!s.TrimEnd().EndsWith(":"))
                throw new Exception("Failed to enable : no password prompt");
            WriteLine(enablePassword);

            s += Read();
            TimeoutMs = oldTimeoutMs;

            Match match = Regex.Match(s, "^.*$", RegexOptions.Multiline | RegexOptions.RightToLeft);
            if (match.Success)
            {
                _prompt = match.Value.Trim(_toTrim);
            }
            
        }

        // Finds the last character of the current prompt returns true if it is '#'
        // retruns false if it is '>' throws an Exception if it is niether
        public bool isEnabled()
        {
            char last = _prompt[_prompt.Length - 1];
            if (last == '#')
            {
                return true;
            }
            else if (last == '>')
            {
                return false;
            }
            else
                throw new Exception("Unusual prompt character found");
        }

        private void ParseTelnet(StringBuilder sb)
        {
            while (tcpSocket.Available > 0)
            {
                int input = tcpSocket.GetStream().ReadByte();
                switch (input)
                {
                    case -1:
                        break;

                    case (int)Verbs.Iac:
                        // interpret as command
                        int inputVerb = tcpSocket.GetStream().ReadByte();
                        if (inputVerb == -1)
                        {
                            break;
                        }

                        switch (inputVerb)
                        {
                            case (int)Verbs.Iac:
                                // literal IAC = 255 escaped, so append char 255 to string
                                sb.Append(inputVerb);
                                break;

                            case (int)Verbs.Do:
                            case (int)Verbs.Dont:
                            case (int)Verbs.Will:
                            case (int)Verbs.Wont:
                                // reply to all commands with "WONT", unless it is SGA (suppres go ahead)
                                int inputoption = tcpSocket.GetStream().ReadByte();
                                if (inputoption == -1)
                                {
                                    break;
                                }

                                tcpSocket.GetStream().WriteByte((byte)Verbs.Iac);

                                if (inputoption == (int)Options.Sga)
                                {
                                    tcpSocket.GetStream().WriteByte(inputVerb == (int)Verbs.Do ? (byte)Verbs.Will : (byte)Verbs.Do);
                                }
                                else
                                {
                                    tcpSocket.GetStream().WriteByte(inputVerb == (int)Verbs.Do ? (byte)Verbs.Wont : (byte)Verbs.Dont);
                                }

                                tcpSocket.GetStream().WriteByte((byte)inputoption);
                                break;
                        }

                        break;

                    default:
                        sb.Append((char)input);
                        break;
                }
            }
        }

        public void Close()
        {
            if (IsConnected)
            {
                tcpSocket.Close();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tcpSocket != null)
                {
                    ((IDisposable)tcpSocket).Dispose();
                    tcpSocket = null;
                }
            }
        }


        #region Private Enums

        enum Verbs
        {
            Will = 251,
            Wont = 252,
            Do = 253,
            Dont = 254,
            Iac = 255
        }

        enum Options
        {
            Sga = 3
        }

        #endregion
    }
}
