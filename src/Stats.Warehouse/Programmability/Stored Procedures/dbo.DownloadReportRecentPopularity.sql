﻿CREATE PROCEDURE [dbo].[DownloadReportRecentPopularity]
AS
BEGIN
	SET NOCOUNT ON;

		SELECT		TOP 100 P.[PackageId]
					,SUM(ISNULL(F.[DownloadCount], 0)) AS 'Downloads'
		FROM		[dbo].[Fact_Download] AS F (NOLOCK)

		INNER JOIN	[dbo].[Dimension_Date] AS D (NOLOCK)
		ON			F.[Dimension_Date_Id] = D.[Id]

		INNER JOIN	[dbo].[Dimension_Package] AS P (NOLOCK)
		ON			F.[Dimension_Package_Id] = P.[Id]

		WHERE		D.[Date] IS NOT NULL
				AND ISNULL(D.[Date], CONVERT(DATE, '1900-01-01')) >= CONVERT(DATE, DATEADD(day, -42, GETDATE()))
				AND ISNULL(D.[Date], CONVERT(DATE, DATEADD(day, 1, GETDATE()))) < CONVERT(DATE, GETDATE())
		GROUP BY	P.[PackageId]
		ORDER BY	[Downloads] DESC
END