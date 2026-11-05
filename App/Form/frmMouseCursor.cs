// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows.Forms;

using MouseWithoutBorders.Class;
using MouseWithoutBorders.Core;

namespace MouseWithoutBorders
{
    public partial class FrmMouseCursor : System.Windows.Forms.Form
    {
        public FrmMouseCursor()
        {
            InitializeComponent();
            BackColor = TransparencyKey;
            Width = Height = 32;
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                // WS_EX_NOACTIVATE | WS_EX_TRANSPARENT: a cursor overlay never takes focus or clicks.
                parameters.ExStyle |= 0x08000020;
                return parameters;
            }
        }

        private void FrmMouseCursor_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
            }
            else
            {
                Common.MouseCursorForm = null;
            }
        }

    }
}
