﻿// <auto-generated> This file has been auto generated by EF Core Power Tools. </auto-generated>
#nullable disable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ClaimTransWorker.Models
{
    public partial class Claims
    {
        public Claims()
        {
            ClaimTransactions = new HashSet<ClaimTransactions>();
        }

        [Key]
        public int ClaimID { get; set; }
        public int? ClaimNumber { get; set; }

        [InverseProperty("Claim")]
        public virtual ICollection<ClaimTransactions> ClaimTransactions { get; set; }
    }
}