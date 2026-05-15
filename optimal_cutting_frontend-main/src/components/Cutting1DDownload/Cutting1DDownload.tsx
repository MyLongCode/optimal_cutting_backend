import { Button } from 'antd';
import { CuttingDownloadPanel } from '../CuttingDownloadPanel/CuttingDownloadPanel';
import { selectCalculateData1D } from '../../features/cutting1DSlice';
import { useAppSelector } from '../../app/hooks';
import { downloadFile1DCutting } from '../../functions/fetchFiles';

export const Cutting1DDownload = () => {
    const dataCalculate1D = useAppSelector(selectCalculateData1D);

    const handlerDownloadPDF = async () => {
        await downloadFile1DCutting(JSON.stringify(dataCalculate1D), 'pdf');
    };

    const handlerDownloadCSV = async () => {
        await downloadFile1DCutting(JSON.stringify(dataCalculate1D), 'csv');
    };

    return (
        <CuttingDownloadPanel>
            <Button type='primary' danger onClick={() => handlerDownloadPDF()}>
                Скачать схему pdf
            </Button>
            <Button danger onClick={() => handlerDownloadCSV()}>
                Скачать схему csv
            </Button>
        </CuttingDownloadPanel>
    );
};
