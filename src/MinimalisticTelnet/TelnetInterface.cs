// minimalistic telnet implementation
// conceived by Tom Janssens on 2007/06/06  for codeproject
//
// http://www.corebvba.be

// Modifications
//
//Date       Person          Description
//========== =============== ============================================================================================================================================
//2007-06-06 Tom Janssens    Shared at https://www.codeproject.com/articles/19071/quick-tool-a-minimalistic-telnet-library
//2013-06-06 jsagara         Implemented IDisposable and other miscellaneous refactoring.
//                               Introduced project to GitHub at https://github.com/jonsagara/MinimalisticTelnet
//2014-05-15 mewiii          Implemented new methods for working with HP switches:
//						         Implements setter/getter methods to change the default timeout value.
//                               Implements new methods for reading until some text you're looking for is found.
//							     Implements Close() method to disconnect from remote host.
//2015-02-05 0xbadf00d       Implements prompt variable and uses it with ReadUntilString to capture returned data using the Command() method.
//                               Implements Enable() & isEnabled() to enable Cisco devices and check status.
//                               Implements char[] _toTrim as null characters seen on some Cisco Nexus devices.
//2017-03-21 Aaron Salisbury Recreate project to target .NET Standard and other miscellaneous refactoring.

using System;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MinimalisticTelnet
{
    public class TelnetConnection : IDisposable
    {
        private static readonly char[] TO_TRIM = { '\n', '\r', '\0' };

        private enum Verbs { Will = 251, Wont, Do, Dont, Iac }
        private enum Options { Sga = 3 }

        private TcpClient _tcpSocket;
        public TcpClient TcpSocket
        {
            set { _tcpSocket = value; }
            get { return _tcpSocket; }
        }

        private int _timeoutMs;
        public int TimeoutMs
        {
            set { _timeoutMs = value; }
            get { return _timeoutMs; }
        }

        private int _readUntilLimit;
        public int ReadUntilLimit
        {
            set { _readUntilLimit = value; }
            get { return _readUntilLimit; }
        }

        private string _prompt;
        public string Prompt
        {
            set { _prompt = value; }
            get { return _prompt; }
        }

        public bool IsConnected
        {
            get { return TcpSocket.Connected; }
        }

        public TelnetConnection(string hostname, int port)
        {
            TimeoutMs = 100;
            ReadUntilLimit = 3;
            Prompt = string.Empty;
            TcpSocket = new TcpClient();
            TcpSocket.ConnectAsync(hostname, port);
        }

        ~TelnetConnection()
        {
            Dispose(false);
        }

        /// <summary>
        /// Parses the server output after the initial connection. It will look for a colon ":" in the screen output, and will send username and password after the colon.
        /// </summary>
        /// <param name="regexMatchPattern">To check if the connection succeeded, server output ending with a "$" or a ">" is assumed.</param>
        /// <returns></returns>
        public string Login(string username, string password, int loginTimeoutMs, string regexMatchPattern = "^.*$")
        {
            int oldTimeoutMs = TimeoutMs;
            TimeoutMs = loginTimeoutMs;

            string serverOutput = Read();
            if (!serverOutput.TrimEnd().EndsWith(":"))
            {
                throw new Exception("Failed to connect : no login prompt");
            }

            WriteLine(username);

            serverOutput += Read();
            if (!serverOutput.TrimEnd().EndsWith(":"))
            {
                throw new Exception("Failed to connect : no password prompt");
            }

            WriteLine(password);

            serverOutput += Read();

            TimeoutMs = oldTimeoutMs;

            Match match = Regex.Match(serverOutput, regexMatchPattern, RegexOptions.Multiline | RegexOptions.RightToLeft);
            if (match.Success)
            {
                Prompt = match.Value.Trim(TO_TRIM);
            }

            return serverOutput;
        }

        public void WriteLine(string cmd)
        {
            Write(cmd + "\n");
        }

        public void Write(string cmd)
        {
            if (!TcpSocket.Connected)
            {
                return;
            }

            byte[] buf = Encoding.ASCII.GetBytes(cmd.Replace("\0xFF", "\0xFF\0xFF"));
            TcpSocket.GetStream().Write(buf, 0, buf.Length);
        }

        /// <summary>
        /// Assumes that if there is no data available for more then TimeOutMs milliseconds, the output is complete.
        /// </summary>
        /// <returns></returns>
        public string Read()
        {
            if (!TcpSocket.Connected)
            {
                return null;
            }

            var sb = new StringBuilder();
            do
            {
                ParseTelnet(sb);
                System.Threading.Tasks.Task.Delay(TimeoutMs).Wait();

            } while (TcpSocket.Available > 0);

            return sb.ToString();
        }

        private void ParseTelnet(StringBuilder sb)
        {
            while (TcpSocket.Available > 0)
            {
                int input = TcpSocket.GetStream().ReadByte();
                switch (input)
                {
                    case -1:
                        break;

                    case (int)Verbs.Iac:
                        // interpret as command
                        int inputVerb = TcpSocket.GetStream().ReadByte();
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
                                int inputoption = TcpSocket.GetStream().ReadByte();
                                if (inputoption == -1)
                                {
                                    break;
                                }

                                TcpSocket.GetStream().WriteByte((byte)Verbs.Iac);

                                if (inputoption == (int)Options.Sga)
                                {
                                    TcpSocket.GetStream().WriteByte(inputVerb == (int)Verbs.Do ? (byte)Verbs.Will : (byte)Verbs.Do);
                                }
                                else
                                {
                                    TcpSocket.GetStream().WriteByte(inputVerb == (int)Verbs.Do ? (byte)Verbs.Wont : (byte)Verbs.Dont);
                                }

                                TcpSocket.GetStream().WriteByte((byte)inputoption);
                                break;
                        }

                        break;

                    default:
                        sb.Append((char)input);
                        break;
                }
            }
        }

        /// <summary>
        /// Useful with Cisco routers that ask for a password only.
        /// </summary>
        public string PasswordOnlyLogin(string Password, int LoginTimeoutMs)
        {
            int oldTimeoutMs = TimeoutMs;
            TimeoutMs = LoginTimeoutMs;
            string s = Read();

            if (!s.TrimEnd().EndsWith(":"))
            {
                throw new Exception("Failed to connect : no password prompt");
            }

            WriteLine(Password);

            s += Read();
            TimeoutMs = oldTimeoutMs;

            return s;
        }

        /// <summary>
        /// Helpful for switches that require that a space be sent prior to entering the password. HP switches output lots of escape sequences and it is unlikely a prompt cleanly ends with #, $, >, etc.
        /// </summary>
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

            Close();

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

        /// <summary>
        /// Read until find text is found in the input stream.
        /// </summary>
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

        /// <summary>
        /// Using the prompt variable, sends the command and reads until the prompt is found.
        /// </summary>
        public string Command(string command)
        {
            WriteLine(command);
            int counter = ReadUntilLimit;
            string s = Read();
            while (!s.Contains(Prompt))
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

        public void Enable(string enablePassword, int EnableTimeoutMs)
        {
            int oldTimeoutMs = TimeoutMs;
            TimeoutMs = EnableTimeoutMs;

            WriteLine("enable");
            string s = Read();

            if (!s.TrimEnd().EndsWith(":"))
            {
                throw new Exception("Failed to enable : no password prompt");
            }

            WriteLine(enablePassword);

            s += Read();
            TimeoutMs = oldTimeoutMs;

            Match match = Regex.Match(s, "^.*$", RegexOptions.Multiline | RegexOptions.RightToLeft);
            if (match.Success)
            {
                Prompt = match.Value.Trim(TO_TRIM);
            }
        }

        /// <summary>
        /// Finds the last character of the current prompt.
        /// </summary>
        /// <returns>True if last character is equal to '#' and False if it is greater than that.</returns>
        public bool IsEnabled()
        {
            char last = Prompt[Prompt.Length - 1];

            if (last == '#')
            {
                return true;
            }
            else if (last == '>')
            {
                return false;
            }
            else
            {
                throw new Exception("Unusual prompt character found");
            }
        }

        public void Close()
        {
            if (IsConnected)
            {
                TcpSocket.Dispose();
                TcpSocket = new TcpClient();
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
                if (TcpSocket != null)
                {
                    ((IDisposable)TcpSocket).Dispose();
                    TcpSocket = null;
                }
            }
        }
    }
}