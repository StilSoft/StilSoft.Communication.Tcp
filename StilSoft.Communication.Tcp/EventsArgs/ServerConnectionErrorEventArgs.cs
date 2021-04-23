﻿//////////////////////////////////////////////////////////////////////////////////
//
// Author: Sase
// Email: sase@stilsoft.net
//
// This software may be modified and distributed under the terms
// of the MIT license. See the LICENSE file for details.
//
//////////////////////////////////////////////////////////////////////////////////


using System;

namespace StilSoft.Communication.Tcp.EventsArgs
{
    public class ErrorEventArgs : EventArgs
    {
        public Exception Exception { get; }


        public ErrorEventArgs(Exception exception)
        {
            this.Exception = exception;
        }
    }
}